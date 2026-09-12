using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Gleam.Core.Execution;
using Gleam.Core.Model;
using Gleam.Core.Stats;
using Gleam.Game;
using Gleam.Integrations;
using Gleam.Services;

namespace Gleam.Automation;

/// <summary>
/// Hands-free mode. Takes the plan the user accepted and does the walking: saddlebag, inn, summoning
/// bell, each retainer's inventory and sell menu, then the glamour dresser. Every item is still
/// re-validated at the moment it is touched; anything a container reveals that was not in the
/// accepted plan pauses for the user instead of acting.
/// </summary>
public enum PilotMode { Clean, Organize }

public sealed partial class AutoPilot : IDisposable
{
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IObjectTable objects;
    private readonly IDataManager data;
    private readonly IChatGui chat;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly RunCoordinator coordinator;
    private readonly VnavmeshIpc nav;
    private readonly LifestreamIpc travel;
    private readonly ItemDatabase db;

    private CancellationTokenSource? cts;
    private uint? saddlebagCommandId;

    public bool IsRunning { get; private set; }
    /// <summary>Which window owns the running screen: the cleaner's or the organizer's.</summary>
    public PilotMode Mode { get; private set; } = PilotMode.Clean;
    public string Status { get; private set; } = string.Empty;
    public string? LastError { get; private set; }

    /// <summary>Whole-run progress for the bar: items planned for this run and how many are finished so far.</summary>
    public int PlannedTotal { get; private set; }
    public int PlannedDone => Mode == PilotMode.Clean
        ? tally.Done + tally.Skipped + tally.Failed + (coordinator.IsRunning ? coordinator.RunDone : 0)
        : movesDone + movesSkipped + (Organizer is { IsRunning: true } ? Organizer.RunDone : 0);

    public AutoPilot(IFramework framework, IClientState clientState, ICondition condition, IObjectTable objects, IDataManager data,
        IChatGui chat, IPluginLog log, Configuration config, RunCoordinator coordinator, VnavmeshIpc nav, LifestreamIpc travel, ItemDatabase db)
    {
        this.framework = framework;
        this.clientState = clientState;
        this.condition = condition;
        this.objects = objects;
        this.data = data;
        this.chat = chat;
        this.log = log;
        this.config = config;
        this.coordinator = coordinator;
        this.nav = nav;
        this.travel = travel;
        this.db = db;
    }

    private AutomationSettings S => config.Automation;

    /// <summary>
    /// What stops a hands-free run from starting at all: vnavmesh missing, because without it Gleam cannot
    /// walk a step, or a game patch this build has not been checked against. Lifestream used to be here too,
    /// which disabled the default "sell to vendors" run for anyone without it, even standing beside a
    /// merchant. Now each leg that needs a teleport checks for it and, without it, leaves that leg's items
    /// waiting while the rest of the run carries on.
    /// </summary>
    public string? MissingDependency() =>
        !nav.IsInstalled ? "the vnavmesh plugin is not installed"
        : !GameVersionGuard.AllowsUnattended(config) ? GameVersionGuard.HeldReason
        : null;

    /// <summary>True when the one thing holding hands-free back is a patch nobody has checked Gleam against yet.</summary>
    public bool HeldByPatch => nav.IsInstalled && !GameVersionGuard.AllowsUnattended(config);

    /// <summary>The player's go-ahead to run hands-free on this patch. The next patch asks again.</summary>
    public void GoAheadOnThisPatch()
    {
        GameVersionGuard.AcceptCurrent(config);
        config.Save(PluginServices.PluginInterface);
        log.Information("Hands-free allowed on game version {Version} by the player", GameVersionGuard.Current() ?? "unknown");
    }

    /// <summary>Whether a leg may teleport somewhere else.</summary>
    private bool CanTravel => S.TravelToInn && travel.IsInstalled;

    private const string NoLifestream = "Lifestream is not installed, so Gleam cannot travel there";

    public void Stop()
    {
        cts?.Cancel();
        nav.Stop();
        travel.Abort();
        coordinator.CancelRun();
    }

    public void Dispose()
    {
        // Cancel only. The run may still be unwinding, and its finally reads the token.
        if (IsRunning) Stop();
    }

    /// <summary>Runs the whole accepted plan, travelling as needed. Returns when done, stopped, or failed.</summary>
    public async Task RunAsync()
    {
        if (IsRunning || coordinator.CurrentPlan is null) return;
        if (MissingDependency() is { } missing) { Fail(missing); return; }
        if (condition[ConditionFlag.InCombat] || condition[ConditionFlag.BoundByDuty]) { Fail("not while in combat or in a duty"); return; }

        var queue = coordinator.BuildQueueFromPlan(r => true);
        if (queue.Count == 0) { Nothing("Nothing is ticked"); return; }
        reviewed = coordinator.CurrentPlan.AllRows.Select(Seen).ToHashSet();
        coordinator.NoteDecisions();

        Mode = PilotMode.Clean;
        PlannedTotal = queue.Count;
        IsRunning = true;
        LastError = null;
        cts?.Dispose();
        cts = new CancellationTokenSource();
        var ct = cts.Token;
        var stopped = false;
        BeginTrip();
        try
        {
            var here = queue.Where(q => q.Kind.IsAlwaysLoaded() && q.Action is not ActionKind.VendorSell and not ActionKind.ExpertDelivery and not ActionKind.MarketList).ToList();
            var listings = queue.Where(q => q.Kind.IsAlwaysLoaded() && q.Action == ActionKind.MarketList).ToList();
            var sells = queue.Where(q => q.Kind.IsAlwaysLoaded() && q.Action == ActionKind.VendorSell).ToList();
            var seals = queue.Where(q => q.Kind.IsAlwaysLoaded() && q.Action == ActionKind.ExpertDelivery).ToList();
            var saddle = queue.Where(q => q.Kind == ContainerKind.Saddlebag).ToList();
            var retainers = queue.Where(q => q.Kind == ContainerKind.Retainer).GroupBy(q => q.Slot.OwnerId).ToDictionary(g => g.Key, g => g.ToList());
            var dresser = queue.Where(q => q.Kind == ContainerKind.GlamourDresser).ToList();

            coordinator.SuppressChatSummary = true;
            tally.Clear();

            // A previous run or the player may have left a retainer window, shop, or dresser open.
            Status = "Closing leftover windows";
            await RecoverUiAsync(ct, atStart: true).ConfigureAwait(false);

            if (here.Count > 0) await Leg("bags", () => Step("Cleaning your bags and armoury chest", () => Execute(here), ct), ct);

            // Go only where the ticked rows are, unless the user asked to sweep everything.
            var sweep = S.VisitContainersWithoutRows && S.UnseenRows != UnseenRowsMode.Skip;
            if (S.OpenSaddlebag && (saddle.Count > 0 || sweep)) await Leg("saddlebag", () => SaddlebagAsync(saddle, ct), ct);

            var needsInn = (S.VisitRetainers && (retainers.Count > 0 || listings.Count > 0 || sweep)) || (S.VisitDresser && (dresser.Count > 0 || sweep));
            if (needsInn)
            {
                var inInn = await Leg("inn", () => TravelToInnAsync(ct), ct);
                if (inInn)
                {
                    if (S.VisitRetainers) await Leg("retainers", () => RetainersAsync(retainers, listings, sells, ct), ct);
                    if (S.VisitDresser) await Leg("dresser", () => DresserAsync(dresser, ct), ct);
                }
            }

            // Items pulled out of retainers (for materia, a vendor, or seals) finish from the bags, retainer closed.
            var broughtBack = coordinator.PendingActions.Where(p => p.Kind.IsAlwaysLoaded() && p.BroughtHome).ToList();
            if (broughtBack.Count > 0)
            {
                coordinator.PendingActions.RemoveAll(broughtBack.Contains);
                await LeaveBellAsync(ct).ConfigureAwait(false);
                sells.AddRange(broughtBack.Where(b => b.Action == ActionKind.VendorSell));
                seals.AddRange(broughtBack.Where(b => b.Action == ActionKind.ExpertDelivery));
                var here2 = broughtBack.Where(b => b.Action is not ActionKind.VendorSell and not ActionKind.ExpertDelivery and not ActionKind.MarketList).ToList();
                if (here2.Count > 0) await Leg("items brought back", () => Step("Finishing items brought back from retainers", () => Execute(here2), ct), ct);
            }

            if (S.VisitGrandCompany && seals.Count > 0) await Leg("expert delivery", () => GrandCompanyAsync(seals, ct), ct);
            if (S.SellAtVendor && sells.Count > 0) await Leg("merchant", () => VendorAsync(sells, ct), ct);

            Status = "Done";
            log.Information("Hands-free clean finished: {Summary}", tally.Summary());
            chat.Print($"Hands-free clean finished: {tally.Summary()}", "Gleam");
            foreach (var line in tally.PendingLines()) chat.Print(line, "Gleam");
            foreach (var line in tally.LegFailures) chat.PrintError(line, "Gleam");
        }
        catch (OperationCanceledException)
        {
            stopped = true;
            Status = "Stopped";
            chat.Print("Stopped. Nothing else was touched.", "Gleam");
        }
        catch (AutoPilotException ex)
        {
            Fail(ex.Message);
        }
        catch (Exception ex)
        {
            log.Error(ex, "AutoPilot crashed");
            Fail(ex.Message);
        }
        finally
        {
            await LeaveGameTidyAsync().ConfigureAwait(false);
            coordinator.SuppressChatSummary = false;
            EndTrip(RunTrigger.HandsFree, PlannedTotal, tally.Done, tally.Skipped, tally.Failed, tally.Pending.Values.Sum(), stopped, tally.LegFailures.Concat(tally.Reasons));
            IsRunning = false;
            await coordinator.RefreshPlanAsync(openWindow: false).ConfigureAwait(false);
        }
    }

    // ---------- tally: one summary at the end instead of one per queue ----------

    private readonly RunTally tally = new();

    private sealed class RunTally
    {
        public int Done, Skipped, Failed;
        public readonly Dictionary<string, int> Pending = new();
        public readonly List<string> LegFailures = new();
        /// <summary>Why individual items failed, for the stats page's "most common reason".</summary>
        public readonly List<string> Reasons = new();

        public void Clear() { Done = Skipped = Failed = 0; Pending.Clear(); LegFailures.Clear(); Reasons.Clear(); }

        public void Add(RunReport? r)
        {
            if (r is null) return;
            Done += r.Done; Skipped += r.Skipped; Failed += r.Failed;
            Reasons.AddRange(r.Results.Where(x => x.Outcome == ActionOutcome.Failed).Select(x => x.Message));
            foreach (var (reason, count) in r.PendingByReason())
                Pending[reason] = Pending.GetValueOrDefault(reason) + count;
        }

        public string Summary()
        {
            var parts = new List<string> { $"{Done} cleaned" };
            if (Skipped > 0) parts.Add($"{Skipped} had moved and {(Skipped == 1 ? "was" : "were")} left alone");
            if (Failed > 0) parts.Add($"{Failed} failed");
            if (LegFailures.Count > 0) parts.Add($"{LegFailures.Count} step{(LegFailures.Count == 1 ? "" : "s")} could not finish");
            var pending = Pending.Values.Sum();
            if (pending > 0) parts.Add($"{pending} still waiting");
            return string.Join(", ", parts) + ".";
        }

        public IEnumerable<string> PendingLines() =>
            Pending.Where(kv => kv.Value > 0).Select(kv => $"{kv.Value} waiting: {(string.IsNullOrEmpty(kv.Key) ? "its storage is not open" : kv.Key)}.");
    }

    /// <summary>
    /// Runs one leg; a failure is recorded and the run moves on to the next leg. Cancellation still stops
    /// everything. Top-level legs are timed for the stats page; a retainer's own leg inside them only counts.
    /// </summary>
    private async Task<bool> Leg(string name, Func<Task> body, CancellationToken ct)
    {
        var top = legDepth == 0;
        if (name.StartsWith("retainer ", StringComparison.Ordinal)) retainersVisited++;
        legDepth++;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var ok = false;
        try
        {
            await body().ConfigureAwait(false);
            ok = true;
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (AutoPilotException ex)
        {
            tally.LegFailures.Add($"{name}: {ex.Message}");
            log.Warning("AutoPilot leg '{Leg}' failed: {Message}", name, ex.Message);
            await RecoverUiAsync(ct).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            tally.LegFailures.Add($"{name}: {ex.Message}");
            log.Error(ex, "AutoPilot leg '{Leg}' crashed", name);
            await RecoverUiAsync(ct).ConfigureAwait(false);
            return false;
        }
        finally
        {
            legDepth--;
            if (top) legs.Add(new LegRecord(LegName(name), Math.Round(clock.Elapsed.TotalSeconds, 1), ok));
        }
    }

    // ---------- the trip, for the stats page ----------

    /// <summary>Set by the plugin: a finished trip, with its stops, teleports and distance walked.</summary>
    public event Action<RunEvent>? TripFinished;

    private readonly List<LegRecord> legs = new();
    private int legDepth;
    private int teleports;
    private int retainersVisited;
    private double walked;
    private Vector3? lastPosition;
    private DateTimeOffset tripStarted;

    /// <summary>Whether a bag window was up when the trip began. The game opens one beside a retainer's inventory.</summary>
    private bool bagsOpenAtStart;

    private static bool BagWindowOpen() => GameUi.AnyVisible("Inventory", "InventoryLarge", "InventoryExpansion");

    private void BeginTrip()
    {
        bagsOpenAtStart = Dalamud.Utility.ThreadSafety.IsMainThread ? BagWindowOpen() : framework.RunOnFrameworkThread(BagWindowOpen).GetAwaiter().GetResult();
        legs.Clear();
        legDepth = 0;
        teleports = 0;
        retainersVisited = 0;
        walked = 0;
        lastPosition = null;
        tripStarted = DateTimeOffset.Now;
        framework.Update += SampleWalk;
    }

    private void EndTrip(RunTrigger trigger, int planned, int done, int skipped, int failed, int waiting, bool stopped, IEnumerable<string> reasons)
    {
        framework.Update -= SampleWalk;
        try
        {
            TripFinished?.Invoke(new RunEvent
            {
                At = DateTimeOffset.Now,
                Trigger = trigger,
                Started = tripStarted,
                Planned = planned,
                Done = done,
                Skipped = skipped,
                Failed = failed,
                Waiting = waiting,
                Stopped = stopped,
                Legs = legs.ToList(),
                Teleports = teleports,
                WalkedYalms = Math.Round(walked, 1),
                RetainersVisited = retainersVisited,
                FailureReasons = reasons.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().Take(5).ToList(),
            });
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Noting the trip for the stats failed");
        }
    }

    /// <summary>Adds up the character's steps while a trip runs. A jump of many yalms at once is a teleport, not a walk.</summary>
    private void SampleWalk(IFramework _)
    {
        var here = objects.LocalPlayer?.Position;
        if (here is null) { lastPosition = null; return; }
        if (lastPosition is { } before)
        {
            var step = Vector3.Distance(here.Value, before);
            if (step < 15f) walked += step;
        }
        lastPosition = here;
    }

    /// <summary>A stop's name as the stats page shows it. Retainers go by "Retainer", never by name.</summary>
    private static string LegName(string name) => name switch
    {
        "bags" => "Bags",
        "saddlebag" => "Saddlebag",
        "inn" => "Travel to the inn",
        "retainers" => "Retainers",
        "dresser" => "Dresser",
        "items brought back" => "Items brought back",
        "expert delivery" => "Grand Company",
        "merchant" => "Merchant",
        _ when name.StartsWith("retainer ", StringComparison.Ordinal) => "Retainer",
        _ => name,
    };

    /// <summary>
    /// Ends the retainer session completely: back to the list, list closed, and the game no longer counting the
    /// character as at the bell. Materia retrieval and several item-menu entries are unavailable until then.
    /// </summary>
    private async Task LeaveBellAsync(CancellationToken ct)
    {
        if (!condition[ConditionFlag.OccupiedSummoningBell]) { await RecoverUiAsync(ct).ConfigureAwait(false); return; }
        Status = "Leaving the summoning bell";
        try { await EnsureRetainerListAsync(ct).ConfigureAwait(false); } catch (AutoPilotException) { /* fall through to closing windows */ }
        await RecoverUiAsync(ct).ConfigureAwait(false);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (condition[ConditionFlag.OccupiedSummoningBell] && DateTime.UtcNow < deadline)
            await Task.Delay(200, ct).ConfigureAwait(false);
        await Task.Delay(500, ct).ConfigureAwait(false);
    }

    /// <summary>Closes whatever menu or window a failed leg left open so the next leg starts clean.</summary>
    private async Task RecoverUiAsync(CancellationToken ct, bool atStart = false)
    {
        nav.Stop();
        // At the start of a run any yes/no question on screen is the player's, not Gleam's. It used to be
        // answered yes, which could confirm a discard the player had not decided on.
        if (atStart && await OnFramework(() => GameUi.IsVisible("SelectYesno")).ConfigureAwait(false))
            throw new AutoPilotException("a yes/no question is open in the game. Answer it, then start again");
        // A retainer's leave prompt left open blocks every later confirmation; answer it before closing windows.
        // Only that one: this used to answer any question on screen, a party invite at the bell included.
        if (await AnswerBuybackPromptAsync().ConfigureAwait(false))
            await Task.Delay(500, ct).ConfigureAwait(false);
        await framework.RunOnFrameworkThread(() =>
        {
            foreach (var addon in new[] { "SelectString", "SelectIconString", "InventoryRetainer", "InventoryRetainerLarge", "RetainerSellList", "RetainerList", "Shop", "GrandCompanySupplyList", "MiragePrismPrismBox", "InventoryBuddy" })
                GameUi.Close(addon);
        }).ConfigureAwait(false);
        await Task.Delay(800, ct).ConfigureAwait(false);
    }

    private async Task Execute(IReadOnlyList<QueuedAction> rows)
    {
        await coordinator.ExecuteQueueAsync(rows, refreshAfter: false, RunTrigger.PartOfTrip).ConfigureAwait(false);
        tally.Add(coordinator.LastReport);
    }

    // ---------- steps ----------

    private async Task OpenSaddlebagAsync(CancellationToken ct)
    {
        if (await OnFramework(() => GameInventoryScanner.IsSaddlebagLoaded() && GameUi.IsVisible("InventoryBuddy")).ConfigureAwait(false)) return;
        await Step("Opening the saddlebag", async () =>
        {
            var id = saddlebagCommandId ??= db.MainCommandIdForEnglishName(S.SaddlebagCommandName);
            if (id is null) throw new AutoPilotException("the saddlebag could not be opened");
            // Right after a teleport or a bell session the game refuses commands for a moment ("while occupied").
            await WaitUntilFreeAsync(ct).ConfigureAwait(false);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await framework.RunOnFrameworkThread(() => GameUi.ExecuteMainCommand(id.Value)).ConfigureAwait(false);
                try
                {
                    await WaitUntil(() => GameInventoryScanner.IsSaddlebagLoaded() && GameUi.IsVisible("InventoryBuddy"), TimeSpan.FromSeconds(4), "the saddlebag to open", ct).ConfigureAwait(false);
                    await Task.Delay(400, ct).ConfigureAwait(false);
                    return;
                }
                catch (AutoPilotException) when (attempt < 2)
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    await WaitUntilFreeAsync(ct).ConfigureAwait(false);
                }
            }
            throw new AutoPilotException("the saddlebag did not open");
        }, ct);
    }

    /// <summary>Waits (bounded) until the character is not occupied, casting or between areas.</summary>
    private async Task WaitUntilFreeAsync(CancellationToken ct)
    {
        for (var i = 0; i < 100; i++)
        {
            var busy = condition[ConditionFlag.Occupied] || condition[ConditionFlag.Occupied30] || condition[ConditionFlag.Occupied33]
                       || condition[ConditionFlag.Occupied38] || condition[ConditionFlag.Occupied39] || condition[ConditionFlag.OccupiedInEvent]
                       || condition[ConditionFlag.OccupiedSummoningBell] || condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]
                       || condition[ConditionFlag.Casting];
            if (!busy) return;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    private async Task SaddlebagAsync(List<QueuedAction> rows, CancellationToken ct)
    {
        await OpenSaddlebagAsync(ct).ConfigureAwait(false);
        if (rows.Count > 0) await Step("Cleaning the saddlebag", () => Execute(rows), ct);
        await HandleUnseen(ContainerKind.Saddlebag, "the saddlebag", ct).ConfigureAwait(false);
        await framework.RunOnFrameworkThread(() => GameUi.Close("InventoryBuddy")).ConfigureAwait(false);
    }

    private async Task TravelToInnAsync(CancellationToken ct)
    {
        if (await OnFramework(IsInInn).ConfigureAwait(false)) return;
        if (!S.TravelToInn) throw new AutoPilotException("not in an inn room, and travelling there is turned off");
        if (!travel.IsInstalled) throw new AutoPilotException($"{NoLifestream}. Start from an inn room and Gleam reaches the bell and the dresser itself");
        await Step("Travelling to an inn", async () =>
        {
            if (!travel.GoToInn(S.InnIndex)) throw new AutoPilotException("the teleport to the inn did not start");
            teleports++;
            await Task.Delay(1500, ct).ConfigureAwait(false);
            await WaitUntil(() => !travel.IsBusy && !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51] && IsInInn(),
                TimeSpan.FromSeconds(S.TravelTimeoutSeconds), "the inn room", ct).ConfigureAwait(false);
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }, ct);
    }

    private async Task RetainersAsync(Dictionary<ulong, List<QueuedAction>> byRetainer, List<QueuedAction> listings, List<QueuedAction> sells, CancellationToken ct)
    {
        var sweep = S.VisitContainersWithoutRows && S.UnseenRows != UnseenRowsMode.Skip;
        if (byRetainer.Count == 0 && listings.Count == 0 && sells.Count == 0 && !sweep) return;
        await EnsureRetainerListAsync(ct).ConfigureAwait(false);

        // The list appears before the server has filled it; selecting too early is silently ignored.
        await WaitForMenu(RetainerListReady, StepTimeout, "the retainer list to fill", ct).ConfigureAwait(false);
        await Task.Delay(1200, ct).ConfigureAwait(false);

        listingFailure = null;
        var order = await OnFramework(RetainerOrder).ConfigureAwait(false);
        var leftAlone = coordinator.EffectiveProfile.ExcludedRetainerIds;
        for (var index = 0; index < order.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var (id, name, freeMarket) = order[index];
            // Skipped, not removed from the list: the list position is how the game selects a retainer.
            if (leftAlone.Contains(id)) continue;
            var rows = byRetainer.GetValueOrDefault(id) ?? new List<QueuedAction>();
            // Only go to a retainer for listings if it has room and listing has not already failed elsewhere.
            var wantsListings = listings.Count > 0 && freeMarket > 0 && listingFailure is null;
            var wantsSells = sells.Count > 0;
            if (rows.Count == 0 && !wantsListings && !wantsSells && !sweep) continue;
            if (!wantsListings && rows.All(r => r.Action == ActionKind.MarketList) && (freeMarket <= 0 || listingFailure is not null) && !sweep)
            {
                log.Information("Skipping {Name}: only listings and {Reason}", name, freeMarket <= 0 ? "no free market slots" : "listing already failed");
                continue;
            }

            var ok = await Leg($"retainer {name}", () => OneRetainerAsync(index, id, name, rows, wantsListings ? listings : new List<QueuedAction>(), sells, ct), ct).ConfigureAwait(false);
            if (!ok) await EnsureRetainerListAsync(ct).ConfigureAwait(false);
        }

        if (listingFailure is not null) tally.LegFailures.Add($"market listing stopped at {listingFailure}");
        await framework.RunOnFrameworkThread(() => GameUi.Close("RetainerList")).ConfigureAwait(false);
    }

    /// <summary>Set when a retainer with free slots listed nothing; further retainers are not visited for listings.</summary>
    private string? listingFailure;

    /// <summary>
    /// Gets the game to the retainer list from wherever the retainer UI currently is: a retainer's
    /// inventory or sell window open, the retainer menu showing, the list already up, or nothing at all.
    /// Interacting with the bell while a retainer session is active does nothing, so this must come first.
    /// </summary>
    private async Task EnsureRetainerListAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var state = await OnFramework(() =>
                GameUi.AnyVisible("InventoryRetainer", "InventoryRetainerLarge", "RetainerSellList", "RetainerSell") ? "inventory"
                : GameUi.SelectStringReady() ? "menu"
                : GameUi.IsVisible("RetainerList") ? "list"
                : "none").ConfigureAwait(false);

            switch (state)
            {
                case "list":
                    return;
                case "inventory":
                    Status = "Closing the open retainer window";
                    await framework.RunOnFrameworkThread(() =>
                    {
                        GameUi.Close("InventoryRetainer"); GameUi.Close("InventoryRetainerLarge");
                        GameUi.Close("RetainerSellList"); GameUi.Close("RetainerSell");
                    }).ConfigureAwait(false);
                    await Task.Delay(800, ct).ConfigureAwait(false);
                    break;
                case "menu":
                    Status = "Leaving the retainer menu";
                    var chosen = await framework.RunOnFrameworkThread(() => GameUi.SelectStringChoose(db.MenuMatcher(S.QuitMenuText))).ConfigureAwait(false);
                    if (chosen < 0) await framework.RunOnFrameworkThread(() => GameUi.Close("SelectString")).ConfigureAwait(false);
                    else await AnswerLeavePromptAsync(ct).ConfigureAwait(false);
                    await Task.Delay(800, ct).ConfigureAwait(false);
                    break;
                default:
                    await WalkToAndInteractAsync(db.LocalizeObjectName(S.BellObjectName), "RetainerList", ct).ConfigureAwait(false);
                    return;
            }
        }
        throw new AutoPilotException("could not get back to the retainer list. Close the retainer windows and run again");
    }

    private async Task OneRetainerAsync(int index, ulong id, string name, List<QueuedAction> allRows, List<QueuedAction> listings, List<QueuedAction> sells, CancellationToken ct)
    {
        var rows = allRows.Where(r => r.Action != ActionKind.MarketList).ToList();
        var ownListings = allRows.Where(r => r.Action == ActionKind.MarketList).ToList();

        await SummonRetainerAsync(index, name, ct).ConfigureAwait(false);
        await OpenRetainerInventoryAsync(id, name, ct).ConfigureAwait(false);

        if (rows.Count > 0) await Step($"Cleaning {name}", () => Execute(rows), ct);
        if (sells.Count > 0)
        {
            // The retainer buys at the vendor price, so bag items marked "sell" are handed over and sold here.
            var batch = sells.ToList();
            await Step($"Selling {batch.Count} item{(batch.Count == 1 ? "" : "s")} through {name}", () => Execute(batch), ct);
            var sold = coordinator.LastReport?.Results.Where(r => r.Outcome == Core.Execution.ActionOutcome.Done).Select(r => r.Action).ToHashSet()
                       ?? new HashSet<QueuedAction>();
            sells.RemoveAll(sold.Contains);
        }
        await HandleUnseen(ContainerKind.Retainer, name, ct).ConfigureAwait(false);
        await CloseRetainerInventoryAsync(name, ct).ConfigureAwait(false);

        // Market listings: first what is in the bags (shared across retainers, each takes what it has room for),
        // then what this retainer holds itself.
        if (listings.Count > 0) await MarketStepAsync(name, S.SellFromBagsMenuText, listings, ct).ConfigureAwait(false);
        if (ownListings.Count > 0) await MarketStepAsync(name, S.SellFromRetainerMenuText, ownListings, ct).ConfigureAwait(false);

        await LeaveRetainerAsync(name, ct).ConfigureAwait(false);
    }

    /// <summary>From the retainer list to the retainer's own menu.</summary>
    private async Task SummonRetainerAsync(int index, string name, CancellationToken ct)
    {
        await Step($"Opening {name}", async () =>
        {
            await WaitForMenu(() => GameUi.IsVisible("RetainerList") && !GameUi.SelectStringReady(), StepTimeout, "the retainer list", ct).ConfigureAwait(false);
            await Task.Delay(1000, ct).ConfigureAwait(false);
            for (var attempt = 0; attempt < 4; attempt++)
            {
                if (attempt == 2)
                {
                    // The list can go unresponsive after a session; reopen it from the bell once.
                    await framework.RunOnFrameworkThread(() => GameUi.Close("RetainerList")).ConfigureAwait(false);
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    await EnsureRetainerListAsync(ct).ConfigureAwait(false);
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                await framework.RunOnFrameworkThread(() => GameUi.RetainerListSelect(S.RetainerListSelect, index)).ConfigureAwait(false);
                try
                {
                    await WaitForMenu(() => GameUi.SelectStringReady(), TimeSpan.FromSeconds(6), $"{name}'s menu", ct).ConfigureAwait(false);
                    return;
                }
                catch (AutoPilotException) when (attempt < 3)
                {
                    await Task.Delay(800, ct).ConfigureAwait(false);
                }
            }
            throw new AutoPilotException($"{name} could not be summoned from the list");
        }, ct);
    }

    /// <summary>From the retainer's menu into its inventory window, confirmed by the game reporting that retainer active.</summary>
    private async Task OpenRetainerInventoryAsync(ulong id, string name, CancellationToken ct)
    {
        await Step($"Opening {name}'s inventory", async () =>
        {
            await ChooseMenu(S.EntrustMenuText, ct).ConfigureAwait(false);
            try
            {
                await WaitUntil(() => GameUi.AnyVisible("InventoryRetainer", "InventoryRetainerLarge") && GameInventoryScanner.IsRetainerOpen(id),
                    StepTimeout, $"{name}'s inventory", ct).ConfigureAwait(false);
            }
            catch (AutoPilotException)
            {
                var (activeId, activeName) = await OnFramework(GameInventoryScanner.ActiveRetainer).ConfigureAwait(false);
                var windowOpen = await OnFramework(() => GameUi.AnyVisible("InventoryRetainer", "InventoryRetainerLarge")).ConfigureAwait(false);
                throw new AutoPilotException($"{name}'s inventory did not open (the game shows {(windowOpen ? activeName : "no retainer")})");
            }
            await Task.Delay(600, ct).ConfigureAwait(false);
        }, ct);
    }

    private async Task CloseRetainerInventoryAsync(string name, CancellationToken ct)
    {
        await framework.RunOnFrameworkThread(() => { GameUi.Close("InventoryRetainer"); GameUi.Close("InventoryRetainerLarge"); }).ConfigureAwait(false);
        await WaitForMenu(() => GameUi.SelectStringReady(), StepTimeout, $"{name}'s menu", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// After a retainer has sold something, quitting asks "unable to process buyback requests once recalled,
    /// proceed?". Answer yes when that prompt shows up within a moment of leaving, and only that prompt: any
    /// other question stops the trip and is left for the player.
    /// </summary>
    private async Task AnswerLeavePromptAsync(CancellationToken ct)
    {
        var unknown = 0;
        for (var i = 0; i < 15; i++)
        {
            if (await OnFramework(() => GameUi.IsVisible("SelectYesno")).ConfigureAwait(false))
            {
                if (await AnswerBuybackPromptAsync().ConfigureAwait(false))
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                    return;
                }
                // The text can lag the window by a frame; only a question that stays unrecognised is someone else's.
                if (++unknown >= 10)
                    throw new AutoPilotException("the game asked a question Gleam does not answer while leaving the retainer. Answer it, then run again");
            }
            else if (await OnFramework(() => GameUi.IsVisible("RetainerList") && !GameUi.SelectStringReady()).ConfigureAwait(false)) return;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    private const string BuybackPromptFragment = "buyback";

    /// <summary>
    /// Answers the retainer's "no buyback once recalled" question, having read it first. Anything else on screen
    /// is somebody else's question and is left alone. Returns whether the prompt was answered.
    /// </summary>
    private Task<bool> AnswerBuybackPromptAsync() => OnFramework(() =>
        GameUi.YesNoPrompt() is { } prompt && db.PromptIsAbout(prompt, BuybackPromptFragment)
        && GameUi.AnswerYesNo(config.Callbacks.YesNoConfirm));

    /// <summary>
    /// Leaves the game as a player would after a trip: the retainer dismissed, and the shop, dresser, saddlebag
    /// and retainer windows closed. Runs after every trip, finished, stopped or failed, and ignores Stop. A Stop
    /// used to end only the walking, and left the retainer summoned with its windows open. Never throws.
    /// </summary>
    private async Task LeaveGameTidyAsync()
    {
        var none = CancellationToken.None;
        try
        {
            nav.Stop();
            // Until the character is off the bell: a Stop can land while a retainer is still greeting, with no
            // window or menu up yet. Looking only for those left the retainer summoned behind its talk bubble.
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var state = await OnFramework(() =>
                    !condition[ConditionFlag.OccupiedSummoningBell] ? "done"
                    : GameUi.AnyVisible("RetainerSell", "RetainerSellList", "InventoryRetainer", "InventoryRetainerLarge") ? "inventory"
                    : GameUi.IsVisible("SelectYesno") ? "yesno"
                    : GameUi.IsVisible("Talk") ? "talk"
                    : GameUi.SelectStringReady() && GameInventoryScanner.ActiveRetainer().Id != 0 ? "menu"
                    : GameInventoryScanner.ActiveRetainer().Id != 0 ? "between"
                    : "list").ConfigureAwait(false);
                if (state is "done" or "list") break;
                switch (state)
                {
                    case "inventory":
                        await framework.RunOnFrameworkThread(() =>
                        {
                            GameUi.Close("RetainerSell"); GameUi.Close("RetainerSellList");
                            GameUi.Close("InventoryRetainer"); GameUi.Close("InventoryRetainerLarge");
                        }).ConfigureAwait(false);
                        break;
                    case "yesno":
                        // The leave question is answered; anything else is the player's, and ends the tidy-up here.
                        if (!await AnswerBuybackPromptAsync().ConfigureAwait(false)) attempt = 8;
                        break;
                    case "talk":
                        // Never click through a cutscene: those use the same bubble.
                        var clicked = await OnFramework(() =>
                            !condition[ConditionFlag.WatchingCutscene] && !condition[ConditionFlag.WatchingCutscene78]
                            && !condition[ConditionFlag.OccupiedInCutSceneEvent] && GameUi.AdvanceTalk()).ConfigureAwait(false);
                        if (!clicked) attempt = 8;
                        break;
                    case "menu":
                        if (await framework.RunOnFrameworkThread(() => GameUi.SelectStringChoose(db.MenuMatcher(S.QuitMenuText))).ConfigureAwait(false) >= 0)
                            await AnswerLeavePromptAsync(none).ConfigureAwait(false);
                        else
                            await framework.RunOnFrameworkThread(() => GameUi.Close("SelectString")).ConfigureAwait(false);
                        break;
                    // "between": a retainer is out but nothing is on screen yet; give it a moment.
                }
                await Task.Delay(800, none).ConfigureAwait(false);
            }
            // The bag window the game opened beside a retainer's inventory stays up after the retainer has gone.
            if (!bagsOpenAtStart)
                await framework.RunOnFrameworkThread(() =>
                {
                    GameUi.Close("Inventory"); GameUi.Close("InventoryLarge"); GameUi.Close("InventoryExpansion");
                }).ConfigureAwait(false);
            await RecoverUiAsync(none).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not close every game window after the trip");
        }
    }

    private async Task LeaveRetainerAsync(string name, CancellationToken ct)
    {
        await Step($"Leaving {name}", async () =>
        {
            await ChooseMenu(S.QuitMenuText, ct).ConfigureAwait(false);
            await AnswerLeavePromptAsync(ct).ConfigureAwait(false);
            await WaitForMenu(() => GameUi.IsVisible("RetainerList") && !GameUi.SelectStringReady(), StepTimeout, "the retainer list", ct).ConfigureAwait(false);
            await Task.Delay(500, ct).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>Opens one of the retainer's sell lists, lists as many rows as there are free market slots, closes it.</summary>
    private async Task MarketStepAsync(string name, string menuFragment, List<QueuedAction> rows, CancellationToken ct)
    {
        var free = await OnFramework(FreeMarketSlots).ConfigureAwait(false);
        if (free <= 0)
        {
            log.Information("{Name} has no free market slots; {Count} listings wait for another retainer", name, rows.Count);
            return;
        }
        await Step($"Opening {name}'s market listings", async () =>
        {
            await ChooseMenu(menuFragment, ct).ConfigureAwait(false);
            await WaitForMenu(() => GameUi.IsVisible("RetainerSellList"), StepTimeout, $"{name}'s sell list", ct).ConfigureAwait(false);
            await Task.Delay(800, ct).ConfigureAwait(false);
        }, ct);

        var batch = rows.Take(free).ToList();
        await Step($"Listing {batch.Count} item{(batch.Count == 1 ? "" : "s")} with {name}", () => Execute(batch), ct);
        var report = coordinator.LastReport;
        var done = report?.Results.Where(r => r.Outcome == Core.Execution.ActionOutcome.Done).Select(r => r.Action).ToHashSet()
                   ?? new HashSet<QueuedAction>();
        rows.RemoveAll(done.Contains);

        // Free slots, nothing listed: the listing step itself is broken, so do not drag it to every other retainer.
        if (done.Count == 0 && report is not null && report.Failed > 0)
        {
            var why = report.Results.FirstOrDefault(r => r.Outcome == Core.Execution.ActionOutcome.Failed)?.Message ?? report.AbortReason;
            listingFailure = $"{name}: {why}";
        }

        await framework.RunOnFrameworkThread(() => { GameUi.Close("RetainerSell"); GameUi.Close("RetainerSellList"); }).ConfigureAwait(false);
        await WaitForMenu(() => GameUi.SelectStringReady(), StepTimeout, $"{name}'s menu", ct).ConfigureAwait(false);
        await Task.Delay(400, ct).ConfigureAwait(false);
    }

    private static unsafe int FreeMarketSlots()
    {
        var rm = FFXIVClientStructs.FFXIV.Client.Game.RetainerManager.Instance();
        if (rm == null) return 0;
        var r = rm->GetActiveRetainer();
        if (r == null || r->RetainerId == 0) return 0;
        return Math.Max(0, Game.GameActions.MarketSlotsPerRetainer - r->MarketItemCount);
    }

    private async Task DresserAsync(List<QueuedAction> rows, CancellationToken ct)
    {
        // The dresser is never cached, so its rows only exist if it was open during the scan.
        if (rows.Count == 0 && !(S.VisitContainersWithoutRows && S.UnseenRows != UnseenRowsMode.Skip)) return;
        await WalkToAndInteractAsync(db.LocalizeObjectName(S.DresserObjectName), "MiragePrismPrismBox", ct).ConfigureAwait(false);
        await WaitUntil(GameInventoryScanner.IsDresserLoaded, StepTimeout, "the dresser to load", ct).ConfigureAwait(false);
        await Task.Delay(800, ct).ConfigureAwait(false);
        if (rows.Count > 0) await Step("Cleaning the glamour dresser", () => Execute(rows), ct);
        await HandleUnseen(ContainerKind.GlamourDresser, "the glamour dresser", ct).ConfigureAwait(false);
        await framework.RunOnFrameworkThread(() => GameUi.Close("MiragePrismPrismBox")).ConfigureAwait(false);
    }

    /// <summary>Selling needs a real merchant: find the named NPC nearby, open its shop, sell from the bags.</summary>
    private async Task VendorAsync(List<QueuedAction> sells, CancellationToken ct)
    {
        // A merchant a few steps away can still be streaming in, so look for a while before deciding there is none.
        var npc = await FindVendorSoonAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        var town = db.LocalizePlaceName(S.VendorAetheryte);
        // Already in the merchant town, a teleport there costs gil and lands in the same zone. It used to happen
        // on every run started near a merchant it had not spotted yet.
        var inTown = await OnFramework(() => db.IsZoneOfAetheryte(clientState.TerritoryType, S.VendorAetheryte)).ConfigureAwait(false);
        if (npc is null && !CanTravel && S.TravelToInn && !inTown)
        {
            var why = "no merchant nearby, and without Lifestream Gleam cannot travel to one";
            tally.Pending[why] = tally.Pending.GetValueOrDefault(why) + sells.Count;
            return;
        }
        if (npc is null && CanTravel && !inTown && !string.IsNullOrWhiteSpace(town))
        {
            await Step($"Teleporting to {db.LocalizePlaceName(S.VendorAetheryte)} for a merchant", async () =>
            {
                var before = clientState.TerritoryType;
                if (!travel.Execute(db.LocalizePlaceName(S.VendorAetheryte))) throw new AutoPilotException("the teleport did not start");
                teleports++;
                await Task.Delay(1500, ct).ConfigureAwait(false);
                await WaitUntil(() => !travel.IsBusy && !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51] && clientState.TerritoryType != before,
                    TimeSpan.FromSeconds(S.TravelTimeoutSeconds), db.LocalizePlaceName(S.VendorAetheryte), ct).ConfigureAwait(false);
                // NPCs stream in after the zone does; wait until the town has actually populated.
                await WaitUntil(() => objects.Count(o => o.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc) >= 5,
                    TimeSpan.FromSeconds(20), "the town to load", ct).ConfigureAwait(false);
                await Task.Delay(1500, ct).ConfigureAwait(false);
            }, ct);
            npc = await FindVendorSoonAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        }
        if (npc is null)
        {
            var nearby = await OnFramework(NearbyObjectNames).ConfigureAwait(false);
            log.Debug("No merchant near {Place}; nearby: {Nearby}", S.VendorAetheryte, string.Join(", ", nearby.Take(8)));
            var reason = inTown ? $"no merchant in sight in {town}. Stand near one and run again" : $"no merchant found near {town}";
            tally.Pending[reason] = tally.Pending.GetValueOrDefault(reason) + sells.Count;
            return;
        }
        var vendorName = npc.Name.TextValue;
        await WalkToAndInteractAsync(vendorName, "Shop", ct, orMenu: true).ConfigureAwait(false);
        if (!await OnFramework(() => GameUi.IsVisible("Shop")).ConfigureAwait(false))
        {
            await Step("Opening the shop", async () =>
            {
                await ChooseMenu(S.VendorMenuText, ct).ConfigureAwait(false);
                await WaitForMenu(() => GameUi.IsVisible("Shop"), StepTimeout, "the shop window", ct).ConfigureAwait(false);
            }, ct);
        }
        await Task.Delay(600, ct).ConfigureAwait(false);
        await Step("Selling to the merchant", () => Execute(sells), ct);
        await framework.RunOnFrameworkThread(() => GameUi.Close("Shop")).ConfigureAwait(false);
    }

    /// <summary>Looks for a merchant until one is in sight or the time is up. NPCs stream in over a few seconds.</summary>
    private async Task<IGameObject?> FindVendorSoonAsync(TimeSpan within, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + within;
        while (true)
        {
            var npc = await OnFramework(FindVendor).ConfigureAwait(false);
            if (npc is not null || DateTime.UtcNow >= deadline) return npc;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Expert Delivery: teleport to the Grand Company's city, reach the HQ, talk to the personnel officer.</summary>
    private async Task GrandCompanyAsync(List<QueuedAction> seals, CancellationToken ct)
    {
        var gc = await OnFramework(GrandCompanyId).ConfigureAwait(false);
        if (gc == 0) throw new AutoPilotException("this character has no Grand Company");

        var officer = await OnFramework(() => FindNearest(db.LocalizeNpcName(S.PersonnelOfficerName))).ConfigureAwait(false);
        if (officer is null)
        {
            if (!S.TravelToInn) throw new AutoPilotException($"No '{db.LocalizeNpcName(S.PersonnelOfficerName)}' nearby and travel is off");
            if (!travel.IsInstalled) throw new AutoPilotException($"no personnel officer nearby. {NoLifestream}");
            if (!S.GcCityAetheryte.TryGetValue(gc, out var city) || string.IsNullOrEmpty(city))
                throw new AutoPilotException("no destination is set for your Grand Company's city");

            // Already in the city, the teleport would land in the same zone and wait for a zone change that never comes.
            var inCity = await OnFramework(() => db.IsZoneOfAetheryte(clientState.TerritoryType, city)).ConfigureAwait(false);
            if (!inCity) await Step($"Teleporting to {city}", async () =>
            {
                var before = clientState.TerritoryType;
                if (!travel.Execute(db.LocalizePlaceName(city))) throw new AutoPilotException("the teleport did not start");
                teleports++;
                await Task.Delay(1500, ct).ConfigureAwait(false);
                await WaitUntil(() => !travel.IsBusy && !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51] && clientState.TerritoryType != before,
                    TimeSpan.FromSeconds(S.TravelTimeoutSeconds), city, ct).ConfigureAwait(false);
                await Task.Delay(1500, ct).ConfigureAwait(false);
            }, ct);

            if (S.GcAethernetShard.TryGetValue(gc, out var shard) && !string.IsNullOrEmpty(shard))
            {
                await Step($"Taking the aethernet to {shard}", async () =>
                {
                    if (!travel.AethernetTeleport(db.LocalizePlaceName(shard))) throw new AutoPilotException($"the aethernet trip to {shard} did not start");
                    teleports++;
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                    await WaitUntil(() => !travel.IsBusy && !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51],
                        TimeSpan.FromSeconds(S.TravelTimeoutSeconds), shard, ct).ConfigureAwait(false);
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }, ct);
            }
        }

        await WalkToAndInteractAsync(db.LocalizeNpcName(S.PersonnelOfficerName), "SelectString", ct, orMenu: true).ConfigureAwait(false);
        await Step("Opening supply missions", async () =>
        {
            await ChooseMenu(S.GcSupplyMenuText, ct).ConfigureAwait(false);
            await WaitForMenu(() => GameUi.IsVisible("GrandCompanySupplyList"), StepTimeout, "the supply window", ct).ConfigureAwait(false);
            await Task.Delay(800, ct).ConfigureAwait(false);
            var ints = S.ExpertDeliveryTabCallback.Split(',').Select(s => int.TryParse(s.Trim(), out var v) ? v : 0).ToList();
            await framework.RunOnFrameworkThread(() => GameUi.FireInts("GrandCompanySupplyList", ints)).ConfigureAwait(false);
            await Task.Delay(800, ct).ConfigureAwait(false);
        }, ct);
        await Step("Turning in for seals", () => Execute(seals), ct);
        await framework.RunOnFrameworkThread(() => GameUi.Close("GrandCompanySupplyList")).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-plans the now-open container with the same rules the review uses and cleans the newly visible
    /// rows the rules would have ticked by default. Off: they wait for the next review.
    /// </summary>
    private async Task HandleUnseen(ContainerKind kind, string what, CancellationToken ct)
    {
        if (S.UnseenRows != UnseenRowsMode.Clean) return;
        await coordinator.RefreshPlanAsync(openWindow: false, focus: kind).ConfigureAwait(false);
        var plan = coordinator.CurrentPlan;
        if (plan is null || !plan.AllRows.Any(r => r.IsExecutable)) return;

        // Only items the player never saw. Anything that was in the review was already accepted or declined,
        // and matching it again here by slot re-ticked items the player had unticked once sorting or a
        // retainer's real slot numbers moved them.
        // What a rule would tick on its own, never a tick carried over from some other moment.
        var queue = coordinator.BuildQueueFromPlan(r => r.IsSuggested && r.Proposal.DefaultChecked && !coordinator.SessionSkips.Contains(r.Key) && !reviewed.Contains(Seen(r)),
            requireChecked: false);
        if (queue.Count == 0) return;
        await Step($"Cleaning {queue.Count} more item{(queue.Count == 1 ? "" : "s")} found in {what}", () => Execute(queue), ct).ConfigureAwait(false);
    }

    /// <summary>Every item the review showed when the run began, by container and item rather than slot.</summary>
    private HashSet<(ContainerKind, ulong, uint, bool)> reviewed = new();

    private static (ContainerKind, ulong, uint, bool) Seen(Core.Planning.PlanRow r) =>
        (r.Item.Slot.Kind, r.Item.Slot.OwnerId, r.Item.ItemId, r.Item.IsHq);

    // ---------- movement & interaction ----------

    private async Task WalkToAndInteractAsync(string objectName, string expectAddon, CancellationToken ct, bool orMenu = false)
    {
        var target = await OnFramework(() => FindNearest(objectName)).ConfigureAwait(false);
        // Bells, dressers and NPCs stream in after a zone loads; one look used to fail for something a few steps away.
        for (var look = 0; target is null && look < 10; look++)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            target = await OnFramework(() => FindNearest(objectName)).ConfigureAwait(false);
        }
        if (target is null)
        {
            var nearby = await OnFramework(NearbyObjectNames).ConfigureAwait(false);
            throw new AutoPilotException($"no {objectName.ToLowerInvariant()} nearby");
        }

        await Step($"Walking to the {objectName.ToLowerInvariant()}", async () =>
        {
            var pos = target.Position;
            if (Distance(pos) > S.InteractRange)
            {
                if (!nav.IsReady)
                {
                    Status = "Waiting for the pathfinder to learn this area";
                    await WaitUntil(() => nav.IsReady, TimeSpan.FromSeconds(45), "vnavmesh to finish building the navmesh for this zone", ct).ConfigureAwait(false);
                }
                if (!nav.MoveCloseTo(pos, S.InteractRange - 0.5f)) throw new AutoPilotException("no path could be found there");
                await Task.Delay(500, ct).ConfigureAwait(false);
                await WaitUntil(() => !nav.IsMoving, TimeSpan.FromSeconds(60), $"arrival at the {objectName.ToLowerInvariant()}", ct).ConfigureAwait(false);
                if (Distance(pos) > S.InteractRange + 1.5f) throw new AutoPilotException($"could not get within reach of the {objectName.ToLowerInvariant()}");
            }
        }, ct);

        await Step($"Using the {objectName.ToLowerInvariant()}", async () =>
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await framework.RunOnFrameworkThread(() => GameUi.Interact(target)).ConfigureAwait(false);
                try
                {
                    await WaitForMenu(() => GameUi.IsVisible(expectAddon) || (orMenu && GameUi.SelectStringReady()), TimeSpan.FromSeconds(6), $"the {objectName.ToLowerInvariant()} to respond", ct).ConfigureAwait(false);
                    return;
                }
                catch (AutoPilotException) when (attempt < 2) { }
            }
            throw new AutoPilotException($"the {objectName.ToLowerInvariant()} did not respond. Close any retainer, shop or dresser window and run again");
        }, ct);
        await Task.Delay(600, ct).ConfigureAwait(false);
    }

    private IGameObject? FindNearest(string name)
    {
        var me = objects.LocalPlayer?.Position ?? Vector3.Zero;
        // The whole table, not one partition: inn fixtures are EventObj entries that can sit anywhere in it.
        return objects
            .Where(o => o.Address != 0 && o.ObjectKind is Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj
                                                        or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.HousingEventObject
                                                        or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc)
            .Where(o => string.Equals(o.Name.TextValue.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => Vector3.Distance(o.Position, me))
            .FirstOrDefault();
    }

    /// <summary>
    /// Nearest NPC that runs a gil shop, by game data rather than by name. A configured name still wins
    /// when such an NPC is present, for players who prefer a particular merchant.
    /// </summary>
    private IGameObject? FindVendor()
    {
        if (!string.IsNullOrWhiteSpace(S.VendorNpcName))
        {
            var named = FindNearest(S.VendorNpcName);
            if (named is not null) return named;
        }
        var me = objects.LocalPlayer?.Position ?? Vector3.Zero;
        return objects
            .Where(o => o.Address != 0 && o.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc && o.IsTargetable && db.IsVendorNpc(o.BaseId))
            .OrderBy(o => Vector3.Distance(o.Position, me))
            .FirstOrDefault();
    }

    /// <summary>Names of interactable objects nearby, NPCs first, for the failure message when a lookup fails.</summary>
    public IReadOnlyList<string> NearbyObjectNames()
    {
        var me = objects.LocalPlayer?.Position ?? Vector3.Zero;
        return objects
            .Where(o => o.Address != 0 && o.ObjectKind is Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj && Vector3.Distance(o.Position, me) < 100)
            .OrderBy(o => Vector3.Distance(o.Position, me))
            .Select(o => $"{o.Name.TextValue} ({o.ObjectKind}, {Vector3.Distance(o.Position, me):0.0}y)")
            .Where(s => !s.StartsWith(" ("))
            .Distinct()
            .ToList();
    }

    private float Distance(Vector3 to)
    {
        var me = objects.LocalPlayer?.Position ?? Vector3.Zero;
        return Vector3.Distance(new Vector3(me.X, 0, me.Z), new Vector3(to.X, 0, to.Z));
    }

    private bool IsInInn()
    {
        var t = clientState.TerritoryType;
        if (t == 0) return false;
        try
        {
            if (data.GetExcelSheet<TerritoryType>()!.TryGetRow(t, out var row) && row.TerritoryIntendedUse.RowId == 2) return true;
        }
        catch { /* fall through */ }
        return FindNearest(db.LocalizeObjectName(S.BellObjectName)) is not null && FindNearest(db.LocalizeObjectName(S.DresserObjectName)) is not null;
    }

    private static unsafe byte GrandCompanyId()
    {
        var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        return ps == null ? (byte)0 : ps->GrandCompany;
    }

    private static unsafe bool RetainerListReady()
    {
        if (!GameUi.IsVisible("RetainerList")) return false;
        var rm = FFXIVClientStructs.FFXIV.Client.Game.RetainerManager.Instance();
        return rm != null && rm->GetRetainerCount() > 0;
    }

    private static unsafe List<(ulong Id, string Name, int FreeMarketSlots)> RetainerOrder()
    {
        var list = new List<(ulong, string, int)>();
        var rm = FFXIVClientStructs.FFXIV.Client.Game.RetainerManager.Instance();
        if (rm == null) return list;
        var count = rm->GetRetainerCount();
        for (uint i = 0; i < count; i++)
        {
            var r = rm->GetRetainerBySortedIndex(i);
            if (r == null || r->RetainerId == 0 || !r->Available) continue;
            list.Add((r->RetainerId, r->NameString, Math.Max(0, Game.GameActions.MarketSlotsPerRetainer - r->MarketItemCount)));
        }
        return list;
    }

    /// <summary>
    /// Chooses the menu entry an English fragment names ("Quit", "your inventory"). On other clients the entry
    /// is recognised by its translation, or by reading the entries back into English.
    /// </summary>
    private async Task ChooseMenu(string englishFragment, CancellationToken ct)
    {
        IReadOnlyList<string> entries = Array.Empty<string>();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            entries = await framework.RunOnFrameworkThread(GameUi.SelectStringEntries).ConfigureAwait(false);
            if (entries.Count > 0) break;
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
        var matches = db.MenuMatcher(englishFragment);
        var chosen = await framework.RunOnFrameworkThread(() => GameUi.SelectStringChoose(matches)).ConfigureAwait(false);
        if (chosen < 0)
        {
            var shown = db.LocalizeMenuText(englishFragment);
            log.Debug("Menu had no '{Fragment}' ({Shown}). Offered: {Entries}", englishFragment, shown, string.Join(" | ", entries));
            throw new AutoPilotException(entries.Count == 0
                ? $"the menu stayed empty, so '{shown}' could not be chosen"
                : $"the menu had no '{shown}' option");
        }
        await Task.Delay(400, ct).ConfigureAwait(false);
    }

    // ---------- plumbing ----------

    private TimeSpan StepTimeout => TimeSpan.FromSeconds(S.StepTimeoutSeconds);

    private async Task Step(string status, Func<Task> body, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (condition[ConditionFlag.InCombat]) throw new AutoPilotException("combat started");
        Status = status;
        log.Information("AutoPilot: {Status}", status);
        await body().ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for a window Gleam has just asked an NPC for, dismissing any talk bubble standing in the way.
    ///
    /// Retainers, merchants and Grand Company officers all say something before their window opens. Gleam
    /// waited for the window alone, so a greeting left a run sitting on "Opening &lt;retainer&gt;" until it timed
    /// out. This is only used while waiting on something Gleam itself started, never on a bubble that was
    /// already on screen when the run began.
    /// </summary>
    private Task WaitForMenu(Func<bool> cond, TimeSpan timeout, string what, CancellationToken ct) =>
        WaitUntil(() =>
        {
            if (cond()) return true;
            // Never click through a cutscene. Those use the same talk bubble, and skipping someone's story
            // would be unforgivable for an inventory plugin.
            if (!condition[ConditionFlag.WatchingCutscene] && !condition[ConditionFlag.WatchingCutscene78]
                && !condition[ConditionFlag.OccupiedInCutSceneEvent])
                GameUi.AdvanceTalk();
            return false;
        }, timeout, what, ct);

    private async Task WaitUntil(Func<bool> cond, TimeSpan timeout, string what, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await OnFramework(cond).ConfigureAwait(false)) return;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        throw new AutoPilotException($"waited too long for {what}");
    }

    private Task<T> OnFramework<T>(Func<T> f) => framework.RunOnFrameworkThread(f);

    /// <summary>A real stop: the run cannot go on. Message completes "Stopped: ...", so it starts lower-case.</summary>
    private void Fail(string message)
    {
        LastError = message;
        Status = $"Stopped: {message}";
        log.Warning("Hands-free run stopped: {Message}", message);
        chat.PrintError($"Stopped: {message}.", "Gleam");
    }

    /// <summary>Nothing to do: not an error, one quiet line.</summary>
    private void Nothing(string message)
    {
        Status = message;
        chat.Print($"{message}.", "Gleam");
    }

    private sealed class AutoPilotException(string message) : Exception(message);
}
