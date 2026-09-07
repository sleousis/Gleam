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
        GameActions actions, StackMerger merger, IRunLog runLog, IOfflineInventorySource offline, IMarketPriceSource market, Action save)
    {
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
                if (merged > 0) chat.Print($"Merged {merged} split stacks.", "Tidy Up");
            }

            var (items, ctx, retainerNames) = await framework.RunOnFrameworkThread(() =>
            {
                var live = focus is null
                    ? scanner.ScanAll(profile.IsContainerEnabled(ContainerKind.Saddlebag), profile.IsContainerEnabled(ContainerKind.Retainer), profile.IsContainerEnabled(ContainerKind.GlamourDresser))
                    : scanner.ScanKind(focus.Value);
                return (live, contextBuilder.Build(), GameInventoryScanner.KnownRetainers());
            }).ConfigureAwait(false);
            RetainerNames = retainerNames;

            var all = new List<ScannedItem>(items);
            if (focus is null) all.AddRange(OfflineItems(items, ctx.CharacterId));

            var withMarket = await AddMarketPricesAsync(all, ctx, profile).ConfigureAwait(false);

            var plan = planner.Build(all, new PlannerInputs
            {
                Context = withMarket,
                Profile = profile,
                InfoLookup = db.Get,
                ProtectList = config.ProtectList,
                AlwaysDiscardList = config.AlwaysDiscardList,
                SessionSkips = SessionSkips,
                IsAvailable = actions.IsContainerAvailable,
                RetainerNames = retainerNames,
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
            Status = $"Scan failed: {ex.Message}";
            chat.PrintError($"Tidy Up scan failed: {ex.Message}", "Tidy Up");
        }
        finally
        {
            scanGate.Release();
        }
    }

    /// <summary>
    /// Everything the cache knows about this character that is not live right now: the saddlebag when
    /// closed, and every retainer's pages. Retainers are separate entries in the cache, recognised by
    /// their items living in retainer pages that name them as owner.
    /// </summary>
    private IEnumerable<ScannedItem> OfflineItems(IReadOnlyList<ScannedItem> live, ulong characterId)
    {
        if (!offline.IsAvailable) yield break;
        var liveKinds = new HashSet<(ContainerKind, ulong)>(live.Select(i => (i.Slot.Kind, i.Slot.OwnerId)));

        IEnumerable<ScannedItem> Filter(IEnumerable<ScannedItem> items)
        {
            foreach (var item in items)
            {
                // Never mix a live container with its cached copy; live always wins.
                if (item.Slot.Kind.IsAlwaysLoaded()) continue;
                if (item.Slot.Kind == ContainerKind.Retainer && item.Slot.OwnerId == 0) continue;
                if (liveKinds.Contains((item.Slot.Kind, item.Slot.OwnerId))) continue;
                if (item.Slot.Kind == ContainerKind.Saddlebag && GameInventoryScanner.IsSaddlebagLoaded()) continue;
                yield return item;
            }
        }

        foreach (var item in Filter(offline.Items(characterId))) yield return item;

        foreach (var entry in offline.Characters())
        {
            if (entry.CharacterId == characterId) continue;
            var items = offline.Items(entry.CharacterId);
            // A retainer's cache entry holds only retainer pages owned by that same id.
            if (items.Count == 0 || !items.All(i => i.Slot.Kind == ContainerKind.Retainer && i.Slot.OwnerId == entry.CharacterId)) continue;
            foreach (var item in Filter(items)) yield return item;
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

    private async Task<ItemContext> AddMarketPricesAsync(IReadOnlyList<ScannedItem> items, ItemContext ctx, Profile profile)
    {
        if (!config.UseUniversalis || !profile.EnabledRules.Contains(Core.Rules.MarketPricePostProcessor.RuleId)) return ctx;
        var world = player.CurrentWorld.ValueNullable?.Name.ExtractText();
        if (string.IsNullOrEmpty(world)) return ctx;
        var ids = items.Select(i => i.ItemId).Distinct().Where(id => db.Get(id)?.IsMarketable == true).ToList();
        if (ids.Count == 0) return ctx;
        IReadOnlyDictionary<uint, MarketPrice> prices = new Dictionary<uint, MarketPrice>();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            prices = await market.GetPricesAsync(ids, world, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Market prices unavailable; rows will say so");
        }
        {
            return new ItemContext
            {
                CharacterId = ctx.CharacterId, CharacterName = ctx.CharacterName,
                GearsetItemIds = ctx.GearsetItemIds, PlateItemIds = ctx.PlateItemIds, PlatesLoaded = ctx.PlatesLoaded,
                JobLevels = ctx.JobLevels, ClassJobCategoryJobs = ctx.ClassJobCategoryJobs,
                MaxGearsetItemLevel = ctx.MaxGearsetItemLevel, RecipesUsing = ctx.RecipesUsing,
                SeasonalItemIds = ctx.SeasonalItemIds, RetiredCurrencyGearIds = ctx.RetiredCurrencyGearIds,
                MarketPrices = prices,
                MarketLookupAttempted = true,
            };
        }
    }

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
        chat.Print($"{name} will never be proposed.", "Tidy Up");
        PlanChanged?.Invoke();
    }

    public void AlwaysDiscard(uint itemId, string name)
    {
        config.AlwaysDiscardList.Add(itemId);
        config.ProtectList.RemoveAll(itemId);
        save();
        chat.Print($"{name} added to the always-discard list.", "Tidy Up");
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
        Status = "Running…";
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
            PendingActions.AddRange(report.Pending);
            Status = report.Summary();

            // "Skipped because it changed" is only useful if we can see *what* changed.
            var skipped = report.Results.Where(r => r.Outcome == ActionOutcome.SkippedChanged).ToList();
            foreach (var r in skipped)
                log.Information("Skipped {Item} at {Slot} (owner {Owner:X}): {Why}", r.Action.ItemName, r.Action.Slot, r.Action.Slot.OwnerId, r.Message);
            if (skipped.Count >= 3)
            {
                chat.Print($"Tidy Up: {skipped.Count} rows did not match their slot. First few:", "Tidy Up");
                foreach (var r in skipped.Take(3))
                    chat.Print($"  {r.Action.ItemName} @ {r.Action.Slot}: {r.Message}", "Tidy Up");
            }

            if (config.ChatSummaryAfterRun && !SuppressChatSummary)
            {
                chat.Print($"Tidy Up: {report.Summary()}.", "Tidy Up");
                foreach (var (reason, count) in report.PendingByReason())
                    chat.Print($"  {count} waiting: {(string.IsNullOrEmpty(reason) ? "container not open" : reason)}.", "Tidy Up");
            }
            if (report.Aborted && report.Failed > 0)
                chat.PrintError($"Tidy Up stopped: {report.AbortReason}. Nothing after that item was touched.", "Tidy Up");
        }
        catch (Exception ex)
        {
            log.Error(ex, "Run failed");
            Status = $"Run failed: {ex.Message}";
            chat.PrintError($"Tidy Up run failed: {ex.Message}", "Tidy Up");
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

    // ---------- resume ----------

    /// <summary>A closed container just opened: re-evaluate it live and show its own confirmation.</summary>
    /// <summary>True while the hands-free pilot owns the flow; container-open triggers stay quiet then.</summary>
    public Func<bool> IsPilotRunning { get; set; } = () => false;

    /// <summary>The pilot reports once at the end; per-queue chat lines would only confuse.</summary>
    public bool SuppressChatSummary { get; set; }

    public async Task OnContainerOpenedAsync(ContainerKind kind)
    {
        if (IsRunning || IsPilotRunning() || !player.IsLoaded) return;
        var profile = EffectiveProfile;
        if (!profile.IsContainerEnabled(kind)) return;

        var hasPending = PendingActions.Any(p => p.Kind == kind);
        if (!hasPending && !profile.IsAutoOpen(kind)) return;

        // Pending actions for this container are dropped: the live re-plan supersedes them and the
        // per-container confirmation is what the user sees. Nothing runs without that click.
        PendingActions.RemoveAll(p => p.Kind == kind);
        await RefreshPlanAsync(openWindow: false, focus: kind).ConfigureAwait(false);
        if (CurrentPlan is not null && CurrentPlan.AllRows.Any(r => r.IsExecutable))
            RequestOpenWindow?.Invoke();
    }

    public void OnActionWindowOpened(string addonName)
    {
        // A vendor or GC officer window opened; if actions were waiting for it, tell the user.
        var waiting = PendingActions.Count(p => p.Action is ActionKind.VendorSell or ActionKind.ExpertDelivery);
        if (waiting > 0) toast.ShowNormal($"Tidy Up: {waiting} accepted items can now be processed. Open /tidyup and accept.");
    }
}
