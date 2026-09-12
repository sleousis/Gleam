using Dalamud.Plugin.Services;
using Gleam.Core.Execution;
using Gleam.Core.Logging;
using Gleam.Core.Model;
using Gleam.Core.Organizer.Capacity;
using Gleam.Core.Organizer.Execution;
using Gleam.Core.Organizer.Model;
using Gleam.Core.Organizer.Solving;
using Gleam.Core.Stats;
using Gleam.Game;

namespace Gleam.Services;

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

    /// <summary>Drops every move approved earlier for a storage that was closed. The next preview proposes afresh.</summary>
    public void ForgetPending()
    {
        PendingMoves.Clear();
        relays.Clear();
        relays.Clear();
        Changed?.Invoke();
    }

    public event Action? Changed;

    /// <summary>Each stack moved, as it happens.</summary>
    public event Action<MoveResult>? MoveFinished;

    /// <summary>A round of moves ended: its report, how it came about, and when it started.</summary>
    public event Action<MoveRunReport, RunTrigger, DateTimeOffset>? MovesFinished;

    /// <summary>Bumped by every change to a layout, so a preview built before the change is known to be stale.</summary>
    public int LayoutVersion { get; private set; }

    public void LayoutChanged()
    {
        LayoutVersion++;
        Changed?.Invoke();
    }

    private (Guid Plan, int Version)? builtFor;

    /// <summary>
    /// The preview on screen was built from the layout as it is now. A preview that was skipped or failed
    /// used to stay on screen, runnable, after the rules it came from had changed.
    /// </summary>
    public bool IsCurrentFresh => Current is not null && config.Organizer.Active is { } a && builtFor == (a.Id, LayoutVersion);

    /// <summary>This character's retainers: the ones a layout can scope to or send things to.</summary>
    public IReadOnlyDictionary<ulong, string> CurrentRetainers
    {
        get
        {
            var mine = new Dictionary<ulong, string>(Game.RetainerDirectory.Current(config));
            if (Snapshot is not null) foreach (var (id, name) in Snapshot.RetainerNames) mine[id] = name;
            return mine;
        }
    }

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
        // Cancelled, not disposed: work still unwinding reads the token, and a disposed one throws.
        ShuttingDown = true;
        runCts?.Cancel();
    }

    /// <summary>The plugin is unloading: no previews, and no waiting moves carried out.</summary>
    public bool ShuttingDown { get; private set; }

    public void OnLogout()
    {
        runCts?.Cancel();
        Current = null;
        Snapshot = null;
        PendingMoves.Clear();
        Changed?.Invoke();
    }

    /// <summary>Scans, applies the active plan and solves. Does not move anything.</summary>
    public async Task PreviewAsync()
    {
        if (ShuttingDown || IsRunning || cleaner.IsRunning || !player.IsLoaded) return;
        // The desired state, the capacity model and the solver run off the game's thread, whoever asked.
        if (framework.IsInFrameworkUpdateThread) await Core.Execution.OffGameThread.Hop();
        if (!await gate.WaitAsync(0).ConfigureAwait(false)) return;
        IsPreviewing = true;
        Status = "Looking through your storage…";
        Changed?.Invoke();
        try
        {
            // The page edits the layout and the never-touch list in place on the game thread while this works on
            // the thread pool, so the work below uses copies taken on the game thread.
            var (live, plan, protect) = await framework.RunOnFrameworkThread(() =>
                (config.Organizer.Active, config.Organizer.Active?.Snapshot(), config.ProtectList.Snapshot())).ConfigureAwait(false);
            if (live is null || plan is null) { Status = "No layout yet"; return; }
            Plan = live;

            var profile = cleaner.EffectiveProfile;
            // "It only ever opens the places ticked here" holds for organizing too. The bags always count.
            bool MayOpen(ContainerKind k) => k == ContainerKind.Inventory || profile.IsContainerEnabled(k);
            var layoutVersion = LayoutVersion;
            var snapshot = await snapshots.CaptureAsync(profile).ConfigureAwait(false);
            Snapshot = snapshot;
            var cid = snapshot.Context.CharacterId;

            var desired = DesiredStateBuilder.Build(
                snapshot.Items, plan, snapshot.Context, db.Get,
                (id, hq) => protect.Contains(id, hq, cid),
                snapshot.RetainerNames.Keys.ToList(),
                profile.ExcludedRetainerIds, MayOpen);

            // Only places the player lets Gleam open, and never a retainer they told it to leave alone.
            var storages = new List<StorageId> { new(ContainerKind.Inventory), new(ContainerKind.Armoury) };
            if (MayOpen(ContainerKind.Saddlebag)) storages.Add(new(ContainerKind.Saddlebag));
            if (MayOpen(ContainerKind.Retainer))
                storages.AddRange(snapshot.RetainerNames.Keys.Where(id => !profile.ExcludedRetainerIds.Contains(id)).Select(id => new StorageId(ContainerKind.Retainer, id)));
            var liveSizes = await framework.RunOnFrameworkThread(mover.LiveSizes).ConfigureAwait(false);
            var spaces = CapacityModel.Build(snapshot.Items, storages, db.Get, liveSizes);

            Current = MoveSolver.Solve(desired, spaces, plan);
            builtFor = (plan.Id, layoutVersion);

            // Waiting moves belong to the plan they came from; only the ones this plan still wants are kept.
            PendingMoveGate.DropStale(PendingMoves, Current.Moves, relays);
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
    public Task RunAsync() => RunMovesAsync(Current?.Moves ?? new List<MoveOp>(), refreshAfter: true, RunTrigger.Organize);

    /// <summary>Set by the plugin so a hands-free run does not trip the "another run is going" guard on itself.</summary>
    public Func<bool> IsPilotRunning { get; set; } = () => false;

    /// <summary>Runs a subset of moves now. The hands-free pilot calls this once per open storage.</summary>
    public async Task RunMovesAsync(IReadOnlyList<MoveOp> ops, bool refreshAfter, RunTrigger trigger = RunTrigger.Organize)
    {
        if (IsRunning || cleaner.IsRunning || ops.Count == 0) return;
        IsRunning = true;
        RunTotal = ops.Count;
        RunDone = 0;
        LastProgress = null;
        Status = "Organizing…";
        Changed?.Invoke();
        var started = DateTimeOffset.Now;
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
                if (r.Status == StepStatus.Done) MoveFinished?.Invoke(r);
                Changed?.Invoke();
            });
            var runFor = player.ContentId;
            var identity = new RunIdentity(player.ContentId, player.CharacterName);
            var report = await executor.ExecuteAsync(ops, identity, runCts.Token, progress).ConfigureAwait(false);
            LastReport = report;
            try { MovesFinished?.Invoke(report, trigger, started); }
            catch (Exception ex) { log.Warning(ex, "Noting the moves for the stats failed"); }
            // Saved on the game's thread, where the settings page also draws.
            if (report.Done > 0 && !config.HasOrganizedOnce) { config.HasOrganizedOnce = true; _ = framework.RunOnFrameworkThread(() => config.Save(PluginServices.PluginInterface)); }
            PendingMoves.RemoveAll(ops.Contains);
            // Never twice, and never for whoever logs in next: matching is by item and quantity.
            if (player.ContentId == runFor) PendingMoveGate.AddWaiting(PendingMoves, report.Pending);
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

    /// <summary>
    /// A saddlebag or retainer opened: approved moves that were waiting for it run now, one round at a time.
    /// A later round only runs once nothing from an earlier round is waiting anywhere, and follows straight
    /// on when it needs the storage that is already open.
    /// </summary>
    public async Task OnContainerOpenedAsync(ContainerKind kind)
    {
        // "Put my things away" off means nothing moves, moves left waiting from an earlier preview included.
        if (!config.UseOrganize || ShuttingDown) return;
        var ran = false;
        for (var round = 0; round < 20; round++)
        {
            if (IsRunning || cleaner.IsRunning || cleaner.IsPilotRunning() || !player.IsLoaded) break;
            var ready = PendingMoveGate.ReadyFor(PendingMoves, kind, mover.IsOpen);
            if (ready.Count == 0) break;
            if (!ran) toast.ShowNormal($"Gleam: putting away {ready.Count} item{(ready.Count == 1 ? "" : "s")} from your earlier preview.");
            ran = true;
            var waitingBefore = PendingMoves.Count;
            var before = LastReport;
            await RunMovesAsync(ready, refreshAfter: false, RunTrigger.StorageOpened).ConfigureAwait(false);
            // A round with a failure, or one that cleared nothing, is where it stops: the next round was sized
            // on this one having drained.
            if (ReferenceEquals(LastReport, before) || LastReport is null || LastReport.Failed > 0 || LastReport.Aborted) break;
            if (PendingMoves.Count >= waitingBefore) break;
        }
        if (ran) await PreviewAsync().ConfigureAwait(false);
    }
}
