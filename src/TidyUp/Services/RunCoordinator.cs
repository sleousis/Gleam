using Dalamud.Plugin.Services;
using TidyUp.Core.Execution;
using TidyUp.Core.Integrations;
using TidyUp.Core.Lists;
using TidyUp.Core.Logging;
using TidyUp.Core.Merging;
using TidyUp.Core.Model;
using TidyUp.Core.Planning;
using TidyUp.Game;

namespace TidyUp.Services;

/// <summary>
/// Owns the current plan and the run lifecycle: scan → plan → (window) → accept → execute → pending →
/// resume when a closed container opens. Everything game-facing goes through the injected adapters.
/// </summary>
public sealed class RunCoordinator : IDisposable
{
    private readonly IFramework framework;
    private readonly IPlayerState player;
    private readonly IChatGui chat;
    private readonly IToastGui toast;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly ItemDatabase db;
    private readonly GameInventoryScanner scanner;
    private readonly ItemContextBuilder contextBuilder;
    private readonly GameActions actions;
    private readonly StackMerger merger;
    private readonly IRunLog runLog;
    private readonly IOfflineInventorySource offline;
    private readonly IMarketPriceSource market;
    private readonly InventorySnapshotService snapshots;
    private readonly RunPlanner planner = new();
    private readonly Action save;

    private CancellationTokenSource? runCts;
    private readonly SemaphoreSlim scanGate = new(1, 1);

    public RunPlan? CurrentPlan { get; private set; }
    public bool IsRunning { get; private set; }
    public string Status { get; private set; } = string.Empty;
    public RunReport? LastReport { get; private set; }
    public ActionResult? LastProgress { get; private set; }
    public int RunTotal { get; private set; }
    public int RunDone { get; private set; }

    /// <summary>Accepted but not yet executable because their container was closed.</summary>
    public List<QueuedAction> PendingActions { get; } = new();

    /// <summary>Row keys unchecked or skipped this session; cleared on logout.</summary>
    public HashSet<string> SessionSkips { get; } = new();

    /// <summary>When set, the confirmation window shows only this container (resume flow).</summary>
    public ContainerKind? FocusContainer { get; private set; }

    /// <summary>Review mode: only what the rules propose, or every item with a default action to tick.</summary>

    public int LastCleanableCount { get; private set; }
    public IReadOnlyDictionary<ulong, string> RetainerNames { get; private set; } = new Dictionary<ulong, string>();

    public event Action? PlanChanged;
    public event Action? RequestOpenWindow;

    public RunCoordinator(IFramework framework, IPlayerState player, IChatGui chat, IToastGui toast, IPluginLog log,
        Configuration config, ItemDatabase db, GameInventoryScanner scanner, ItemContextBuilder contextBuilder,
        GameActions actions, StackMerger merger, IRunLog runLog, IOfflineInventorySource offline, IMarketPriceSource market,
        InventorySnapshotService snapshots, Action save)
    {
        this.snapshots = snapshots;
        this.framework = framework;
        this.player = player;
        this.chat = chat;
        this.toast = toast;
        this.log = log;
        this.config = config;
        this.db = db;
        this.scanner = scanner;
        this.contextBuilder = contextBuilder;
        this.actions = actions;
        this.merger = merger;
        this.runLog = runLog;
        this.offline = offline;
        this.market = market;
        this.save = save;
    }

    public void Dispose()
    {
        runCts?.Cancel();
        runCts?.Dispose();
    }

    public Profile EffectiveProfile => config.Profiles.Effective(player.ContentId);

    public void OnLogout()
    {
        SessionSkips.Clear();
        PendingActions.Clear();
        CurrentPlan = null;
        FocusContainer = null;
        PlanChanged?.Invoke();
    }

    // ---------- scanning & planning ----------

    /// <summary>Full scan and plan. Merges stacks first when the profile says so. Optionally opens the window.</summary>
    public async Task RefreshPlanAsync(bool openWindow, ContainerKind? focus = null)
    {
        if (IsRunning || !player.IsLoaded) return;
        // Two overlapping scans would both run the stack-merge pass and race each other's moves.
        if (!await scanGate.WaitAsync(0).ConfigureAwait(false)) return;
        Status = "Scanning…";
        FocusContainer = focus;
        try
        {
            var profile = EffectiveProfile;
            if (profile.StackMergeBeforeScan && focus is null)
            {
                var merged = await StackMergeAsync().ConfigureAwait(false);
                if (merged > 0) chat.Print($"Merged {merged} split stack{(merged == 1 ? "" : "s")}.", "Gleam");
            }

            var snapshot = await snapshots.CaptureAsync(profile, focus).ConfigureAwait(false);
            RetainerNames = snapshot.RetainerNames;
            var withMarket = snapshot.Context;

            var plan = planner.Build(snapshot.Items, new PlannerInputs
            {
                Context = withMarket,
                Profile = profile,
                InfoLookup = db.Get,
                ProtectList = config.ProtectList,
                AlwaysDiscardList = config.AlwaysDiscardList,
                SessionSkips = SessionSkips,
                IsAvailable = actions.IsContainerAvailable,
                RetainerNames = snapshot.RetainerNames,
                IncludeUnproposed = true,
            });

            if (focus is null && config.ShowAltSections) AddAltPreviews(plan, withMarket, profile);

            CurrentPlan = plan;
            LastCleanableCount = plan.AllRows.Count(r => r.IsExecutable);
            Status = string.Empty;
            PlanChanged?.Invoke();
            if (openWindow) RequestOpenWindow?.Invoke();
        }
        catch (Exception ex)
        {
            log.Error(ex, "Scan failed");
            Status = "The scan did not finish";
            chat.PrintError("The scan did not finish. Details are in the Dalamud log.", "Gleam");
        }
        finally
        {
            scanGate.Release();
        }
    }

    private void AddAltPreviews(RunPlan plan, ItemContext ctx, Profile profile)
    {
        if (!offline.IsAvailable) return;
        try
        {
            foreach (var alt in offline.Characters())
            {
                if (alt.IsRetainer || alt.CharacterId == ctx.CharacterId) continue;
                var items = offline.Items(alt.CharacterId);
                if (items.Count == 0) continue;
                // Retainer entries are folded into the owner's plan above; only real alts belong here.
                if (items.All(i => i.Slot.Kind == ContainerKind.Retainer)) continue;
                var altPlan = planner.Build(items, new PlannerInputs
                {
                    Context = ctx, Profile = profile, InfoLookup = db.Get,
                    ProtectList = config.ProtectList, AlwaysDiscardList = config.AlwaysDiscardList,
                    IsAvailable = (_, _) => false, RetainerNames = RetainerNames,
                });
                var preview = new AltPreview { CharacterId = alt.CharacterId, CharacterName = alt.Name };
                preview.Proposals.AddRange(altPlan.AllRows.Select(r => r.Proposal));
                if (preview.Proposals.Count > 0) plan.Alts.Add(preview);
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Alt preview failed");
        }
    }

    /// <summary>Where market prices come from: the character's home world, e.g. "Omega".</summary>
    public string MarketScope => snapshots.MarketScope;

    public async Task<int> CountCleanableAsync()
    {
        await RefreshPlanAsync(openWindow: false).ConfigureAwait(false);
        return LastCleanableCount;
    }

    public async Task<int> StackMergeAsync()
    {
        var items = await framework.RunOnFrameworkThread(() => scanner.ScanAll(true, true, false)).ConfigureAwait(false);
        var moves = StackMergePlanner.Plan(items, db.Get);
        if (moves.Count == 0) return 0;
        var accepted = await merger.ExecuteAsync(moves, TimeSpan.FromMilliseconds(config.Callbacks.RateLimitMs), CancellationToken.None).ConfigureAwait(false);
        return accepted;
    }

    // ---------- row-level user actions ----------

    public void SkipRow(PlanRow row)
    {
        row.Checked = false;
        SessionSkips.Add(row.Key);
        PlanChanged?.Invoke();
    }

    public void Protect(uint itemId, string name)
    {
        config.ProtectList.Add(itemId);
        config.AlwaysDiscardList.RemoveAll(itemId);
        save();
        if (CurrentPlan is not null)
            foreach (var s in CurrentPlan.Sections) s.Rows.RemoveAll(r => r.Item.ItemId == itemId);
        chat.Print($"{name} will never be touched.", "Gleam");
        PlanChanged?.Invoke();
    }

    public void AlwaysDiscard(uint itemId, string name)
    {
        config.AlwaysDiscardList.Add(itemId);
        config.ProtectList.RemoveAll(itemId);
        save();
        chat.Print($"{name} will always be cleaned.", "Gleam");
    }

    // ---------- execution ----------

    /// <summary>Checked, executable rows of the current plan as a queue, optionally filtered.</summary>
    public List<QueuedAction> BuildQueueFromPlan(Func<PlanRow, bool> filter) =>
        CurrentPlan is null
            ? new List<QueuedAction>()
            : CurrentPlan.AllRows.Where(r => r.Checked && r.IsExecutable && filter(r)).Select(QueuedAction.FromRow).ToList();

    public void RaiseOpenWindow() => RequestOpenWindow?.Invoke();

    public async Task AcceptAsync()
    {
        if (IsRunning || CurrentPlan is null) return;
        var queue = BuildQueueFromPlan(_ => true);
        if (queue.Count == 0) return;

        // Rows the user accepted earlier for still-closed containers stay queued alongside the new ones.
        var merged = PendingActions.Where(p => !queue.Any(q => q.Slot == p.Slot)).Concat(queue).ToList();
        PendingActions.Clear();
        await ExecuteQueueAsync(merged, refreshAfter: true).ConfigureAwait(false);
    }

    /// <summary>Runs a queue now. Used by Accept and by the hands-free pilot for one container at a time.</summary>
    public async Task ExecuteQueueAsync(IReadOnlyList<QueuedAction> queue, bool refreshAfter)
    {
        if (IsRunning || queue.Count == 0) return;
        IsRunning = true;
        RunTotal = queue.Count;
        RunDone = 0;
        LastProgress = null;
        Status = "Cleaning…";
        PlanChanged?.Invoke();
        runCts?.Dispose();
        runCts = new CancellationTokenSource();
        try
        {
            var engine = new ExecutionEngine(actions, runLog, new RealDelay(),
                new ExecutionOptions
                {
                    RateLimit = TimeSpan.FromMilliseconds(config.Callbacks.RateLimitMs),
                    OnMateriaFailure = config.ActWhenMateriaFails ? MateriaFailurePolicy.ActAnyway : MateriaFailurePolicy.LeaveItem,
                    MarketStackSize = config.MarketListStackSize,
                });
            var progress = new Progress<ActionResult>(r =>
            {
                LastProgress = r;
                if (r.IsTerminal) RunDone++;
                PlanChanged?.Invoke();
            });
            var identity = new RunIdentity(player.ContentId, player.CharacterName);
            var report = await engine.ExecuteAsync(queue, identity, runCts.Token, progress).ConfigureAwait(false);
            LastReport = report;
            if (report.Done > 0 && !config.HasCleanedOnce) { config.HasCleanedOnce = true; save(); }
            PendingActions.AddRange(report.Pending);
            PendingActions.AddRange(report.Moved);
            if (config.SortAfterRun) await SortTouchedAsync(report).ConfigureAwait(false);
            Status = report.Summary();

            log.Information("Clean finished: {Summary}", report.Summary());
            foreach (var r in report.Results.Where(r => r.Outcome == ActionOutcome.SkippedChanged))
                log.Debug("Left alone {Item} at {Slot}: {Why}", r.Action.ItemName, r.Action.Slot, r.Message);

            // The hands-free pilot prints one summary for the whole trip; a plain clean reports here.
            if (config.ChatSummaryAfterRun && !SuppressChatSummary)
            {
                chat.Print($"{report.Summary()}.", "Gleam");
                foreach (var (reason, count) in report.PendingByReason())
                    chat.Print($"{count} waiting: {(string.IsNullOrEmpty(reason) ? "its storage is not open" : reason)}.", "Gleam");
                if (report.Moved.Any(m => m.BroughtHome))
                    chat.Print("Close the retainer and clean again to finish the items brought back.", "Gleam");
                if (report.Moved.Any(m => !m.BroughtHome))
                    chat.Print("Some stacks are only partly listed. A retainer with free market slots can finish them.", "Gleam");
                if (report.Aborted && report.Failed > 0)
                    chat.PrintError($"Stopped: {report.AbortReason}. Nothing after that was touched.", "Gleam");
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Run failed");
            Status = "The run did not finish";
            chat.PrintError("The run did not finish. Details are in the Dalamud log.", "Gleam");
        }
        finally
        {
            IsRunning = false;
            FocusContainer = null;
            // Re-plan so the window shows what is left rather than a stale list.
            if (refreshAfter) await RefreshPlanAsync(openWindow: false).ConfigureAwait(false);
        }
    }

    public void CancelRun() => runCts?.Cancel();

    /// <summary>Runs the game's own sort on every container something was just removed from. Containers are still open at this point.</summary>
    private async Task SortTouchedAsync(RunReport report)
    {
        var kinds = report.Results.Where(r => r.Outcome is ActionOutcome.Done or ActionOutcome.Moved).Select(r => r.Action.Kind)
            .Concat(report.Moved.Select(m => m.Kind))
            .Distinct()
            .Where(k => k != ContainerKind.GlamourDresser)
            .ToList();
        if (kinds.Count == 0) return;
        Status = "Sorting…";
        foreach (var kind in kinds)
        {
            try { await actions.SortContainerAsync(kind, runCts?.Token ?? CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.Warning(ex, "Sorting {Kind} failed", kind); }
        }
    }

    // ---------- resume ----------

    /// <summary>True while the hands-free pilot owns the flow; container-open triggers stay quiet then.</summary>
    public Func<bool> IsPilotRunning { get; set; } = () => false;

    /// <summary>The pilot reports once at the end; per-queue chat lines would only confuse.</summary>
    public bool SuppressChatSummary { get; set; }

    /// <summary>A closed container just opened: re-evaluate it live and show its own confirmation.</summary>
    public async Task OnContainerOpenedAsync(ContainerKind kind)
    {
        if (IsRunning || IsPilotRunning() || !player.IsLoaded) return;
        var profile = EffectiveProfile;
        if (!profile.IsContainerEnabled(kind)) return;

        // Only this retainer's rows are in play; the other retainers keep theirs.
        var owner = kind == ContainerKind.Retainer ? GameInventoryScanner.ActiveRetainer().Id : 0UL;
        bool Here(QueuedAction p) => p.Kind == kind && (owner == 0 || p.Slot.OwnerId == owner);

        var hasPending = PendingActions.Any(Here);
        if (!hasPending && !profile.IsAutoOpen(kind)) return;

        // Pending actions for this container are dropped: the live re-plan supersedes them and the
        // per-container confirmation is what the user sees. Nothing runs without that click.
        PendingActions.RemoveAll(Here);
        await RefreshPlanAsync(openWindow: false, focus: kind).ConfigureAwait(false);
        if (CurrentPlan is not null && CurrentPlan.AllRows.Any(r => r.IsExecutable))
            RequestOpenWindow?.Invoke();
    }

    public void OnActionWindowOpened(string addonName)
    {
        // A vendor or GC officer window opened; if actions were waiting for it, tell the user.
        var waiting = PendingActions.Count(p => p.Action is ActionKind.VendorSell or ActionKind.ExpertDelivery or ActionKind.MarketList);
        if (waiting > 0) toast.ShowNormal($"Gleam: {waiting} waiting item{(waiting == 1 ? "" : "s")} can be finished here. /gleam to clean.");
    }
}
