using Dalamud.Plugin.Services;
using TidyUp.Core.Execution;
using TidyUp.Core.Logging;
using TidyUp.Core.Model;
using TidyUp.Core.Organizer.Capacity;
using TidyUp.Core.Organizer.Execution;
using TidyUp.Core.Organizer.Model;
using TidyUp.Core.Organizer.Solving;
using TidyUp.Game;

namespace TidyUp.Services;

/// <summary>
/// Owns the organizer flow: snapshot → desired state → solved moves → preview → run. Approved moves whose
/// storage is closed wait and run by themselves when that storage opens. Never runs alongside a clean.
/// </summary>
public sealed class OrganizerCoordinator : IDisposable
{
    private readonly IFramework framework;
    private readonly IPlayerState player;
    private readonly IChatGui chat;
    private readonly IToastGui toast;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly ItemDatabase db;
    private readonly InventorySnapshotService snapshots;
    private readonly IMoveActions mover;
    private readonly IMoveLog moveLog;
    private readonly RunCoordinator cleaner;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource? runCts;

    public OrganizerCoordinator(IFramework framework, IPlayerState player, IChatGui chat, IToastGui toast, IPluginLog log, Configuration config,
        ItemDatabase db, InventorySnapshotService snapshots, IMoveActions mover, IMoveLog moveLog, RunCoordinator cleaner)
    {
        this.framework = framework;
        this.player = player;
        this.chat = chat;
        this.toast = toast;
        this.log = log;
        this.config = config;
        this.db = db;
        this.snapshots = snapshots;
        this.mover = mover;
        this.moveLog = moveLog;
        this.cleaner = cleaner;
    }

    public SolveResult? Current { get; private set; }
    public InventorySnapshot? Snapshot { get; private set; }
    public OrganizerPlan? Plan { get; private set; }
    public bool IsRunning { get; private set; }
    public bool IsPreviewing { get; private set; }
    public string Status { get; private set; } = string.Empty;
    public MoveRunReport? LastReport { get; private set; }
    public MoveResult? LastProgress { get; private set; }
    public int RunTotal { get; private set; }
    public int RunDone { get; private set; }

    /// <summary>Approved moves whose storage was closed when the run reached them.</summary>
    public List<MoveOp> PendingMoves { get; } = new();

    /// <summary>Where each relay's first leg left its stack, kept across runs so the second leg takes exactly that one.</summary>
    private readonly RelayLedger relays = new();

    /// <summary>The same move, whichever solve produced it: every solve hands out fresh move ids.</summary>
    private static bool SameMove(MoveOp a, MoveOp b) =>
        a.Item.Slot == b.Item.Slot && a.Item.ItemId == b.Item.ItemId && a.Item.Quantity == b.Item.Quantity && a.To == b.To && a.Leg == b.Leg;

    /// <summary>Drops every move approved earlier for a storage that was closed. The next preview proposes afresh.</summary>
    public void ForgetPending()
    {
        PendingMoves.Clear();
        relays.Clear();
        relays.Clear();
        Changed?.Invoke();
    }

    public event Action? Changed;

    /// <summary>
    /// Every retainer that can be named, not only the ones the last scan happened to see. Before this, a
    /// layout drawn at login showed raw ids because no snapshot had been taken yet.
    /// </summary>
    public IReadOnlyDictionary<ulong, string> RetainerNames
    {
        get
        {
            var known = Game.RetainerDirectory.Names(config);
            if (Snapshot is null || Snapshot.RetainerNames.Count == 0) return known;
            var merged = new Dictionary<ulong, string>(known);
            foreach (var (id, name) in Snapshot.RetainerNames) merged[id] = name;
            return merged;
        }
    }

    public void Dispose()
    {
        runCts?.Cancel();
        runCts?.Dispose();
    }

    public void OnLogout()
    {
        Current = null;
        Snapshot = null;
        PendingMoves.Clear();
        Changed?.Invoke();
    }

    /// <summary>Scans, applies the active plan and solves. Does not move anything.</summary>
    public async Task PreviewAsync()
    {
        if (IsRunning || cleaner.IsRunning || !player.IsLoaded) return;
        if (!await gate.WaitAsync(0).ConfigureAwait(false)) return;
        IsPreviewing = true;
        Status = "Looking through your storage…";
        Changed?.Invoke();
        try
        {
            var plan = config.Organizer.Active;
            if (plan is null) { Status = "No layout yet"; return; }
            Plan = plan;

            var snapshot = await snapshots.CaptureAsync(cleaner.EffectiveProfile).ConfigureAwait(false);
            Snapshot = snapshot;
            var cid = snapshot.Context.CharacterId;

            var desired = DesiredStateBuilder.Build(
                snapshot.Items, plan, snapshot.Context, db.Get,
                (id, hq) => config.ProtectList.Contains(id, hq, cid),
                snapshot.RetainerNames.Keys.ToList());

            var storages = new List<StorageId> { new(ContainerKind.Inventory), new(ContainerKind.Armoury), new(ContainerKind.Saddlebag) };
            storages.AddRange(snapshot.RetainerNames.Keys.Select(id => new StorageId(ContainerKind.Retainer, id)));
            var liveSizes = await framework.RunOnFrameworkThread(mover.LiveSizes).ConfigureAwait(false);
            var spaces = CapacityModel.Build(snapshot.Items, storages, db.Get, liveSizes);

            Current = MoveSolver.Solve(desired, spaces, plan);

            // Waiting moves belong to the plan they came from. Once the layout, a rule or the storage has
            // changed, only the ones this plan still wants are kept: the rest would carry out a layout the
            // player has since rewritten, the next time some unrelated saddlebag or retainer opened.
            foreach (var stale in PendingMoves.Where(p => !Current.Moves.Any(m => SameMove(m, p))).ToList())
            {
                PendingMoves.Remove(stale);
                relays.Forget(stale.MoveId);
            }
            Status = string.Empty;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Organizer preview failed");
            Status = "The moves could not be worked out";
        }
        finally
        {
            IsPreviewing = false;
            gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Runs the current preview's moves. Only what the user saw; closed storages leave their moves waiting.</summary>
    public Task RunAsync() => RunMovesAsync(Current?.Moves ?? new List<MoveOp>(), refreshAfter: true);

    /// <summary>Set by the plugin so a hands-free run does not trip the "another run is going" guard on itself.</summary>
    public Func<bool> IsPilotRunning { get; set; } = () => false;

    /// <summary>Runs a subset of moves now. The hands-free pilot calls this once per open storage.</summary>
    public async Task RunMovesAsync(IReadOnlyList<MoveOp> ops, bool refreshAfter)
    {
        if (IsRunning || cleaner.IsRunning || ops.Count == 0) return;
        IsRunning = true;
        RunTotal = ops.Count;
        RunDone = 0;
        LastProgress = null;
        Status = "Organizing…";
        Changed?.Invoke();
        runCts?.Dispose();
        runCts = new CancellationTokenSource();
        try
        {
            var executor = new MoveExecutor(mover, moveLog, new RealDelay(), new MoveExecutorOptions
            {
                RateLimit = TimeSpan.FromMilliseconds(config.Callbacks.RateLimitMs),
            }, relays);
            var progress = new Progress<MoveResult>(r =>
            {
                LastProgress = r;
                if (r.IsTerminal) RunDone++;
                Changed?.Invoke();
            });
            var runFor = player.ContentId;
            var identity = new RunIdentity(player.ContentId, player.CharacterName);
            var report = await executor.ExecuteAsync(ops, identity, runCts.Token, progress).ConfigureAwait(false);
            LastReport = report;
            if (report.Done > 0 && !config.HasOrganizedOnce) { config.HasOrganizedOnce = true; config.Save(PluginServices.PluginInterface); }
            PendingMoves.RemoveAll(ops.Contains);
            // Never twice, and never for whoever logs in next: matching is by item and quantity.
            if (player.ContentId == runFor)
                foreach (var waiting in report.Pending)
                    if (!PendingMoves.Any(p => SameMove(p, waiting))) PendingMoves.Add(waiting);
            Status = report.Summary();

            log.Information("Organize finished: {Summary}", report.Summary());
            if (config.ChatSummaryAfterRun && !IsPilotRunning())
            {
                chat.Print($"{report.Summary()}.", "Gleam");
                foreach (var (reason, count) in report.PendingByReason())
                    chat.Print($"{count} waiting: {(string.IsNullOrEmpty(reason) ? "its storage is not open" : reason)}.", "Gleam");
                if (report.Aborted && report.Failed > 0)
                    chat.PrintError($"Stopped: {report.AbortReason}. Nothing after that was moved.", "Gleam");
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Organizer run failed");
            Status = "The run did not finish";
            chat.PrintError("Organizing did not finish. Details are in the Dalamud log.", "Gleam");
        }
        finally
        {
            IsRunning = false;
            Changed?.Invoke();
            if (refreshAfter) await PreviewAsync().ConfigureAwait(false);
        }
    }

    public void CancelRun() => runCts?.Cancel();

    /// <summary>Whether a storage can be moved into right now, so an open saddlebag needs no trip.</summary>
    public bool IsOpen(StorageId storage) => mover.IsOpen(storage);

    /// <summary>A saddlebag or retainer opened: approved moves that were waiting for it run now.</summary>
    public async Task OnContainerOpenedAsync(ContainerKind kind)
    {
        if (IsRunning || cleaner.IsRunning || cleaner.IsPilotRunning() || !player.IsLoaded) return;
        var ready = PendingMoves.Where(m => m.RequiresOpen is { } s && s.Kind == kind && mover.IsOpen(s)).ToList();
        if (ready.Count == 0) return;
        toast.ShowNormal($"Gleam: putting away {ready.Count} item{(ready.Count == 1 ? "" : "s")} from your earlier preview.");
        await RunMovesAsync(ready, refreshAfter: true).ConfigureAwait(false);
    }
}
