using Dalamud.Plugin.Services;
using Gleam.Core.Execution;
using Gleam.Core.Integrations;
using Gleam.Core.Lists;
using Gleam.Core.Logging;
using Gleam.Core.Merging;
using Gleam.Core.Model;
using Gleam.Core.Planning;
using Gleam.Core.Stats;
using Gleam.Game;

namespace Gleam.Services;

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

    /// <summary>True while a scan is under way, so the refresh control can say it is working.</summary>
    public bool IsScanning { get; private set; }
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
    private IReadOnlyDictionary<ulong, string> scannedRetainers = new Dictionary<ulong, string>();

    /// <summary>Every retainer that can be named, whether or not the last scan reached it.</summary>
    public IReadOnlyDictionary<ulong, string> RetainerNames
    {
        get
        {
            var known = Game.RetainerDirectory.Names(config);
            if (scannedRetainers.Count == 0) return known;
            var merged = new Dictionary<ulong, string>(known);
            foreach (var (id, name) in scannedRetainers) merged[id] = name;
            return merged;
        }
    }

    /// <summary>The last look through the player's things threw. The window offers to try again.</summary>
    public bool ScanFailed { get; private set; }

    /// <summary>Market prices came back for this scan. When not, "no listings" would be a guess.</summary>
    public bool PricesKnown { get; private set; }

    /// <summary>Set by the plugin: whether an organize run is moving things right now.</summary>
    public Func<bool> IsOrganizing { get; set; } = () => false;

    public event Action? PlanChanged;
    public event Action? RequestOpenWindow;

    /// <summary>Each item a run finished, as it happens. The stats page counts along with it.</summary>
    public event Action<ActionResult>? ActionFinished;

    /// <summary>A run ended: its report, how it came about, and when it started.</summary>
    public event Action<RunReport, RunTrigger, DateTimeOffset>? RunFinished;

    /// <summary>A full look through the player's things finished.</summary>
    public event Action<InventorySnapshot, RunPlan>? ScanFinished;

    /// <summary>The player started a run from this plan: what each rule suggested and what they kept ticked.</summary>
    public event Action<RunPlan>? DecisionsTaken;

    public void NoteDecisions()
    {
        if (CurrentPlan is { } plan) DecisionsTaken?.Invoke(plan);
    }

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
        // Cancelled, not disposed: work still unwinding reads the token, and a disposed one throws.
        ShuttingDown = true;
        runCts?.Cancel();
    }

    /// <summary>The plugin is unloading: work still unwinding must not start a scan, touch the game or write files.</summary>
    public bool ShuttingDown { get; private set; }

    public Profile EffectiveProfile => config.Profiles.Effective(player.ContentId);

    public void OnLogout()
    {
        // A run must not carry on into the next character's inventory.
        runCts?.Cancel();
        LastCleanableCount = 0;
        SessionSkips.Clear();
        PendingActions.Clear();
        CurrentPlan = null;
        FocusContainer = null;
        PlanChanged?.Invoke();
    }

    // ---------- scanning & planning ----------

    /// <summary>Full scan and plan. Merges stacks first when the profile says so. Optionally opens the window.</summary>
    /// <param name="userAsked">
    /// True only when the player pressed Look again or asked for a scan. Split stacks are merged only then:
    /// merging moves items, and a scan after login or after a duty must not change anything unasked.
    /// </param>
    /// <returns>Whether a fresh plan came out of it. A skipped or failed scan leaves the old one in place.</returns>
    public async Task<bool> RefreshPlanAsync(bool openWindow, ContainerKind? focus = null, bool userAsked = false)
    {
        if (ShuttingDown || IsRunning || !player.IsLoaded) return false;
        // Off the game's thread before any of the work. The game memory reads inside hop back on their own.
        if (framework.IsInFrameworkUpdateThread) await Core.Execution.OffGameThread.Hop();
        // Two overlapping scans would both run the stack-merge pass and race each other's moves.
        if (!await scanGate.WaitAsync(0).ConfigureAwait(false)) return false;
        IsScanning = true;
        Status = "Scanning…";
        FocusContainer = focus;
        try
        {
            var profile = EffectiveProfile;
            if (profile.StackMergeBeforeScan && focus is null && userAsked)
            {
                var merged = await StackMergeAsync().ConfigureAwait(false);
                if (merged > 0) chat.Print($"Merged {merged} split stack{(merged == 1 ? "" : "s")}.", "Gleam");
            }

            var snapshot = await snapshots.CaptureAsync(profile, focus).ConfigureAwait(false);
            scannedRetainers = snapshot.RetainerNames;
            // A scan can reach retainers the game is not currently listing, so remember what it found.
            if (Game.RetainerDirectory.Learn(config, snapshot.RetainerNames)) save();
            var withMarket = snapshot.Context;

            // The planner runs on the thread pool while the settings page and the row menus edit these lists in
            // place on the game thread, so it plans from copies taken there.
            var lists = await framework.RunOnFrameworkThread(() => (
                Protect: config.ProtectList.Snapshot(),
                Discard: config.AlwaysDiscardList.Snapshot(),
                Skips: (IReadOnlySet<string>)new HashSet<string>(SessionSkips))).ConfigureAwait(false);
            var plan = planner.Build(snapshot.Items, new PlannerInputs
            {
                Context = withMarket,
                Profile = profile,
                InfoLookup = db.Get,
                ProtectList = lists.Protect,
                AlwaysDiscardList = lists.Discard,
                SessionSkips = lists.Skips,
                IsAvailable = actions.IsContainerAvailable,
                // The game only lists retainers once a bell has been used since logging in. The names Gleam
                // remembered fill the gap, so a cached retainer's section is still titled with its name.
                RetainerNames = RetainerNames,
                IncludeUnproposed = true,
            });

            if (focus is null && config.ShowAltSections) AddAltPreviews(plan, withMarket, profile);

            // The player's own ticks survive a re-scan, and loot that lands while the window is open starts
            // unticked unless the player asked for this scan.
            RescanTicks.CarryOver(CurrentPlan, plan, userAsked);
            // Unticks follow the item to its new slot, so the after-ventures clean and hands-free runs still see them.
            foreach (var key in Core.Planning.RescanTicks.CarrySkips(CurrentPlan, plan, SessionSkips)) SessionSkips.Add(key);

            CurrentPlan = plan;
            if (focus is null)
            {
                try { ScanFinished?.Invoke(snapshot, plan); }
                catch (Exception ex) { log.Warning(ex, "Noting the scan for the stats failed"); }
            }
            // Only what a rule suggested counts as junk. Every item is listed for hand-picking, and counting
            // those made the server info bar and the toasts call nearly the whole inventory junk.
            LastCleanableCount = plan.AllRows.Count(r => r.IsExecutable && r.IsSuggested);
            Status = string.Empty;
            ScanFailed = false;
            PricesKnown = withMarket.MarketLookupAttempted && withMarket.MarketPrices.Count > 0;
            PlanChanged?.Invoke();
            if (openWindow) RequestOpenWindow?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Scan failed");
            Status = "The scan did not finish";
            ScanFailed = true;
            chat.PrintError("The scan did not finish. Details are in the Dalamud log.", "Gleam");
            return false;
        }
        finally
        {
            IsScanning = false;
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
        // Only the places the player lets Gleam open, and never a retainer they left alone: a merge moves items too.
        var profile = EffectiveProfile;
        var items = await framework.RunOnFrameworkThread(() =>
        {
            var retainerOk = profile.IsContainerEnabled(ContainerKind.Retainer)
                             && !profile.ExcludedRetainerIds.Contains(GameInventoryScanner.ActiveRetainer().Id);
            return scanner.ScanAll(profile.IsContainerEnabled(ContainerKind.Saddlebag), retainerOk, false);
        }).ConfigureAwait(false);
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
    /// <param name="requireChecked">
    /// False only for work done while the player is away, which picks its own rows (what a rule would tick
    /// on its own) rather than trusting ticks that may be the player's, made for a different moment.
    /// </param>
    public List<QueuedAction> BuildQueueFromPlan(Func<PlanRow, bool> filter, bool requireChecked = true) =>
        CurrentPlan is null
            ? new List<QueuedAction>()
            : PlanQueue.Build(CurrentPlan.AllRows, filter, requireChecked);

    public void RaiseOpenWindow() => RequestOpenWindow?.Invoke();

    public async Task AcceptAsync()
    {
        if (IsRunning || CurrentPlan is null) return;
        var queue = BuildQueueFromPlan(_ => true);
        if (queue.Count == 0) return;

        // Rows accepted earlier for a container that was closed ride along, unless the player has since said
        // no to that item. An unticked row for the same item is a no, wherever the item now sits.
        var merged = PendingMerge.Merge(PendingActions, queue, CurrentPlan.AllRows);
        PendingActions.Clear();
        NoteDecisions();
        await ExecuteQueueAsync(merged, refreshAfter: true, RunTrigger.ByHand).ConfigureAwait(false);
    }

    /// <summary>Drops everything accepted earlier for a container that was closed. Those items return to the review.</summary>
    public void ForgetPending()
    {
        PendingActions.Clear();
        PlanChanged?.Invoke();
    }

    /// <summary>Runs a queue now. Used by Accept and by the hands-free pilot for one container at a time.</summary>
    public async Task ExecuteQueueAsync(IReadOnlyList<QueuedAction> queue, bool refreshAfter, RunTrigger trigger = RunTrigger.ByHand)
    {
        // Never alongside an organize run: both would be moving the same bags at once.
        if (IsRunning || queue.Count == 0 || IsOrganizing()) return;
        IsRunning = true;
        RunTotal = queue.Count;
        RunDone = 0;
        LastProgress = null;
        Status = "Cleaning…";
        PlanChanged?.Invoke();
        var started = DateTimeOffset.Now;
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
                if (r.Outcome == ActionOutcome.Done) ActionFinished?.Invoke(r);
                PlanChanged?.Invoke();
            });
            var runFor = player.ContentId;
            var identity = new RunIdentity(player.ContentId, player.CharacterName);
            var report = await engine.ExecuteAsync(queue, identity, runCts.Token, progress).ConfigureAwait(false);
            LastReport = report;
            try { RunFinished?.Invoke(report, trigger, started); }
            catch (Exception ex) { log.Warning(ex, "Noting the run for the stats failed"); }
            if (report.Done > 0 && !config.HasCleanedOnce) { config.HasCleanedOnce = true; save(); }
            // A run that ends after a logout or a character switch must not leave its waiting items behind for
            // whoever logs in next: matching is by item and quantity, so another character's stack would do.
            if (player.ContentId == runFor)
            {
                // Only what is not waiting already: a trip and the after-ventures clean hand the same rows back more
                // than once, and the "N waiting" count and the next run used to count them twice.
                foreach (var waiting in report.Pending.Concat(report.Moved))
                    if (!PendingActions.Contains(waiting)) PendingActions.Add(waiting);
            }
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
        if (ShuttingDown || IsRunning || IsPilotRunning() || !player.IsLoaded || !config.UseClean) return;
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
        // Only for junk a rule found. Every discardable item is a row, so this used to open for any container that
        // held anything at all.
        if (CurrentPlan is not null && CurrentPlan.AllRows.Any(r => r.IsExecutable && r.IsSuggested))
            RequestOpenWindow?.Invoke();
    }

    public void OnActionWindowOpened(string addonName)
    {
        // A vendor or GC officer window opened; if actions were waiting for it, tell the user.
        var waiting = PendingActions.Count(p => p.Action is ActionKind.VendorSell or ActionKind.ExpertDelivery or ActionKind.MarketList);
        if (waiting > 0) toast.ShowNormal($"Gleam: {waiting} waiting item{(waiting == 1 ? "" : "s")} can be finished here. /gleam to clean.");
    }
}
