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

    public event Action? Changed;
    public event Action? RequestOpenWindow;

    public IReadOnlyDictionary<ulong, string> RetainerNames => Snapshot?.RetainerNames ?? new Dictionary<ulong, string>();

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
            if (plan is null) { Status = "No layout yet."; return; }
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
            Status = string.Empty;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Organizer preview failed");
            Status = $"Could not work out the moves: {ex.Message}";
        }
        finally
        {
            IsPreviewing = false;
            gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Runs the current preview's moves. Only what the user saw; closed storages leave their moves waiting.</summary>
    public Task RunAsync() => RunAsync(Current?.Moves ?? new List<MoveOp>());

    private async Task RunAsync(IReadOnlyList<MoveOp> ops)
    {
        if (IsRunning || cleaner.IsRunning || cleaner.IsPilotRunning() || ops.Count == 0) return;
        IsRunning = true;
        RunTotal = ops.Count;
        RunDone = 0;
        LastProgress = null;
        Status = "Moving…";
        Changed?.Invoke();
        runCts?.Dispose();
        runCts = new CancellationTokenSource();
        try
        {
            var executor = new MoveExecutor(mover, moveLog, new RealDelay(), new MoveExecutorOptions
            {
                RateLimit = TimeSpan.FromMilliseconds(config.Callbacks.RateLimitMs),
            });
            var progress = new Progress<MoveResult>(r =>
            {
                LastProgress = r;
                if (r.IsTerminal) RunDone++;
                Changed?.Invoke();
            });
            var identity = new RunIdentity(player.ContentId, player.CharacterName);
            var report = await executor.ExecuteAsync(ops, identity, runCts.Token, progress).ConfigureAwait(false);
            LastReport = report;
            PendingMoves.RemoveAll(ops.Contains);
            PendingMoves.AddRange(report.Pending);
            Status = report.Summary();

            if (config.ChatSummaryAfterRun)
            {
                chat.Print($"Tidy Up: {report.Summary()}.", "Tidy Up");
                foreach (var (reason, count) in report.PendingByReason())
                    chat.Print($"  {count} waiting: {(string.IsNullOrEmpty(reason) ? "storage not open" : reason)}.", "Tidy Up");
            }
            if (report.Aborted && report.Failed > 0)
                chat.PrintError($"Tidy Up stopped organising after repeated failures: {report.AbortReason}.", "Tidy Up");
        }
        catch (Exception ex)
        {
            log.Error(ex, "Organizer run failed");
            Status = $"Run failed: {ex.Message}";
            chat.PrintError($"Tidy Up could not finish organising: {ex.Message}", "Tidy Up");
        }
        finally
        {
            IsRunning = false;
            Changed?.Invoke();
            await PreviewAsync().ConfigureAwait(false);
        }
    }

    public void CancelRun() => runCts?.Cancel();

    /// <summary>A saddlebag or retainer opened: approved moves that were waiting for it run now.</summary>
    public async Task OnContainerOpenedAsync(ContainerKind kind)
    {
        if (IsRunning || cleaner.IsRunning || cleaner.IsPilotRunning() || !player.IsLoaded) return;
        var ready = PendingMoves.Where(m => m.RequiresOpen is { } s && s.Kind == kind && mover.IsOpen(s)).ToList();
        if (ready.Count == 0) return;
        toast.ShowNormal($"Tidy Up: putting away {ready.Count} item{(ready.Count == 1 ? "" : "s")} you approved earlier.");
        await RunAsync(ready).ConfigureAwait(false);
    }
}
