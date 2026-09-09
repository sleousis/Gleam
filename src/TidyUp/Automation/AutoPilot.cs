using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using TidyUp.Core.Execution;
using TidyUp.Core.Model;
using TidyUp.Game;
using TidyUp.Integrations;
using TidyUp.Services;

namespace TidyUp.Automation;

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

    public string? MissingDependency()
    {
        if (S.TravelToInn && !travel.IsInstalled) return "the Lifestream plugin is not installed";
        if (!nav.IsInstalled) return "the vnavmesh plugin is not installed";
        return null;
    }

    public void Stop()
    {
        cts?.Cancel();
        nav.Stop();
        travel.Abort();
        coordinator.CancelRun();
    }

    public void Dispose()
    {
        if (IsRunning) Stop();
        cts?.Dispose();
        cts = null;
    }

    /// <summary>Runs the whole accepted plan, travelling as needed. Returns when done, stopped, or failed.</summary>
    public async Task RunAsync()
    {
        if (IsRunning || coordinator.CurrentPlan is null) return;
        if (MissingDependency() is { } missing) { Fail(missing); return; }
        if (condition[ConditionFlag.InCombat] || condition[ConditionFlag.BoundByDuty]) { Fail("not while in combat or in a duty"); return; }

        var queue = coordinator.BuildQueueFromPlan(r => true);
        if (queue.Count == 0) { Nothing("Nothing is ticked"); return; }

        Mode = PilotMode.Clean;
        PlannedTotal = queue.Count;
        IsRunning = true;
        LastError = null;
        cts?.Dispose();
        cts = new CancellationTokenSource();
        var ct = cts.Token;
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
            await RecoverUiAsync(ct).ConfigureAwait(false);

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
            chat.Print($"Hands-free clean finished: {tally.Summary()}", "Tidy Up");
            foreach (var line in tally.PendingLines()) chat.Print(line, "Tidy Up");
            foreach (var line in tally.LegFailures) chat.PrintError(line, "Tidy Up");
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped";
            chat.Print("Stopped. Nothing else was touched.", "Tidy Up");
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
            nav.Stop();
            coordinator.SuppressChatSummary = false;
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

        public void Clear() { Done = Skipped = Failed = 0; Pending.Clear(); LegFailures.Clear(); }

        public void Add(RunReport? r)
        {
            if (r is null) return;
            Done += r.Done; Skipped += r.Skipped; Failed += r.Failed;
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

    /// <summary>Runs one leg; a failure is recorded and the run moves on to the next leg. Cancellation still stops everything.</summary>
    private async Task<bool> Leg(string name, Func<Task> body, CancellationToken ct)
    {
        try
        {
            await body().ConfigureAwait(false);
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
    }

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
    private async Task RecoverUiAsync(CancellationToken ct)
    {
        nav.Stop();
        // A retainer's leave prompt left open blocks every later confirmation; answer it before closing windows.
        if (await OnFramework(() => GameUi.IsVisible("SelectYesno") && (GameUi.IsVisible("RetainerList") || GameUi.SelectStringReady())).ConfigureAwait(false))
        {
            await framework.RunOnFrameworkThread(() => GameUi.FireInts("SelectYesno", [config.Callbacks.YesNoConfirm])).ConfigureAwait(false);
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        await framework.RunOnFrameworkThread(() =>
        {
            foreach (var addon in new[] { "SelectString", "SelectIconString", "InventoryRetainer", "InventoryRetainerLarge", "RetainerSellList", "RetainerList", "Shop", "GrandCompanySupplyList", "MiragePrismPrismBox", "InventoryBuddy" })
                GameUi.Close(addon);
        }).ConfigureAwait(false);
        await Task.Delay(800, ct).ConfigureAwait(false);
    }

    private async Task Execute(IReadOnlyList<QueuedAction> rows)
    {
        await coordinator.ExecuteQueueAsync(rows, refreshAfter: false).ConfigureAwait(false);
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
        await Step("Travelling to an inn", async () =>
        {
            if (!travel.GoToInn(S.InnIndex)) throw new AutoPilotException("the teleport to the inn did not start");
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
        await WaitUntil(RetainerListReady, StepTimeout, "the retainer list to fill", ct).ConfigureAwait(false);
        await Task.Delay(1200, ct).ConfigureAwait(false);

        listingFailure = null;
        var order = await OnFramework(RetainerOrder).ConfigureAwait(false);
        for (var index = 0; index < order.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var (id, name, freeMarket) = order[index];
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
                    var chosen = await framework.RunOnFrameworkThread(() => GameUi.SelectStringChoose(db.LocalizeMenuText(S.QuitMenuText))).ConfigureAwait(false);
                    if (chosen < 0) await framework.RunOnFrameworkThread(() => GameUi.Close("SelectString")).ConfigureAwait(false);
                    else await AnswerLeavePromptAsync(ct).ConfigureAwait(false);
                    await Task.Delay(800, ct).ConfigureAwait(false);
                    break;
                default:
                    await WalkToAndInteractAsync(db.LocalizeObjectName(S.BellObjectName), "RetainerList", ct).ConfigureAwait(false);
                    return;
            }
        }
        throw new AutoPilotException("could not get back to the retainer list; close the retainer windows and run again");
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
        if (listings.Count > 0) await MarketStepAsync(name, db.LocalizeMenuText(S.SellFromBagsMenuText), listings, ct).ConfigureAwait(false);
        if (ownListings.Count > 0) await MarketStepAsync(name, db.LocalizeMenuText(S.SellFromRetainerMenuText), ownListings, ct).ConfigureAwait(false);

        await LeaveRetainerAsync(name, ct).ConfigureAwait(false);
    }

    /// <summary>From the retainer list to the retainer's own menu.</summary>
    private async Task SummonRetainerAsync(int index, string name, CancellationToken ct)
    {
        await Step($"Opening {name}", async () =>
        {
            await WaitUntil(() => GameUi.IsVisible("RetainerList") && !GameUi.SelectStringReady(), StepTimeout, "the retainer list", ct).ConfigureAwait(false);
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
                    await WaitUntil(() => GameUi.SelectStringReady(), TimeSpan.FromSeconds(6), $"{name}'s menu", ct).ConfigureAwait(false);
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
            await ChooseMenu(db.LocalizeMenuText(S.EntrustMenuText), ct).ConfigureAwait(false);
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
        await WaitUntil(() => GameUi.SelectStringReady(), StepTimeout, $"{name}'s menu", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// After a retainer has sold something, quitting asks "unable to process buyback requests once recalled,
    /// proceed?". Answer yes when that prompt shows up within a moment of leaving.
    /// </summary>
    private async Task AnswerLeavePromptAsync(CancellationToken ct)
    {
        for (var i = 0; i < 15; i++)
        {
            if (await OnFramework(() => GameUi.IsVisible("SelectYesno")).ConfigureAwait(false))
            {
                await framework.RunOnFrameworkThread(() => GameUi.FireInts("SelectYesno", [config.Callbacks.YesNoConfirm])).ConfigureAwait(false);
                await Task.Delay(500, ct).ConfigureAwait(false);
                return;
            }
            if (await OnFramework(() => GameUi.IsVisible("RetainerList") && !GameUi.SelectStringReady()).ConfigureAwait(false)) return;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    private async Task LeaveRetainerAsync(string name, CancellationToken ct)
    {
        await Step($"Leaving {name}", async () =>
        {
            await ChooseMenu(db.LocalizeMenuText(S.QuitMenuText), ct).ConfigureAwait(false);
            await AnswerLeavePromptAsync(ct).ConfigureAwait(false);
            await WaitUntil(() => GameUi.IsVisible("RetainerList") && !GameUi.SelectStringReady(), StepTimeout, "the retainer list", ct).ConfigureAwait(false);
            await Task.Delay(500, ct).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>Opens one of the retainer's sell lists, lists as many rows as there are free market slots, closes it.</summary>
    private async Task MarketStepAsync(string name, string menuText, List<QueuedAction> rows, CancellationToken ct)
    {
        var free = await OnFramework(FreeMarketSlots).ConfigureAwait(false);
        if (free <= 0)
        {
            log.Information("{Name} has no free market slots; {Count} listings wait for another retainer", name, rows.Count);
            return;
        }
        await Step($"Opening {name}'s market listings", async () =>
        {
            await ChooseMenu(menuText, ct).ConfigureAwait(false);
            await WaitUntil(() => GameUi.IsVisible("RetainerSellList"), StepTimeout, $"{name}'s sell list", ct).ConfigureAwait(false);
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
        await WaitUntil(() => GameUi.SelectStringReady(), StepTimeout, $"{name}'s menu", ct).ConfigureAwait(false);
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
        var npc = await OnFramework(FindVendor).ConfigureAwait(false);
        if (npc is null && S.TravelToInn && !string.IsNullOrWhiteSpace(db.LocalizePlaceName(S.VendorAetheryte)))
        {
            await Step($"Teleporting to {db.LocalizePlaceName(S.VendorAetheryte)} for a merchant", async () =>
            {
                var before = clientState.TerritoryType;
                if (!travel.Execute(db.LocalizePlaceName(S.VendorAetheryte))) throw new AutoPilotException("the teleport did not start");
                await Task.Delay(1500, ct).ConfigureAwait(false);
                await WaitUntil(() => !travel.IsBusy && !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51] && clientState.TerritoryType != before,
                    TimeSpan.FromSeconds(S.TravelTimeoutSeconds), db.LocalizePlaceName(S.VendorAetheryte), ct).ConfigureAwait(false);
                // NPCs stream in after the zone does; wait until the town has actually populated.
                await WaitUntil(() => objects.Count(o => o.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc) >= 5,
                    TimeSpan.FromSeconds(20), "the town to load", ct).ConfigureAwait(false);
                await Task.Delay(1500, ct).ConfigureAwait(false);
            }, ct);
            npc = await OnFramework(FindVendor).ConfigureAwait(false);
        }
        if (npc is null)
        {
            var nearby = await OnFramework(NearbyObjectNames).ConfigureAwait(false);
            log.Debug("No merchant near {Place}; nearby: {Nearby}", S.VendorAetheryte, string.Join(", ", nearby.Take(8)));
            var reason = $"no merchant found near {db.LocalizePlaceName(S.VendorAetheryte)}";
            tally.Pending[reason] = tally.Pending.GetValueOrDefault(reason) + sells.Count;
            return;
        }
        var vendorName = npc.Name.TextValue;
        await WalkToAndInteractAsync(vendorName, "Shop", ct, orMenu: true).ConfigureAwait(false);
        if (!await OnFramework(() => GameUi.IsVisible("Shop")).ConfigureAwait(false))
        {
            await Step("Opening the shop", async () =>
            {
                await ChooseMenu(db.LocalizeMenuText(S.VendorMenuText), ct).ConfigureAwait(false);
                await WaitUntil(() => GameUi.IsVisible("Shop"), StepTimeout, "the shop window", ct).ConfigureAwait(false);
            }, ct);
        }
        await Task.Delay(600, ct).ConfigureAwait(false);
        await Step("Selling to the merchant", () => Execute(sells), ct);
        await framework.RunOnFrameworkThread(() => GameUi.Close("Shop")).ConfigureAwait(false);
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
            if (!S.GcCityAetheryte.TryGetValue(gc, out var city) || string.IsNullOrEmpty(city))
                throw new AutoPilotException("no destination is set for your Grand Company's city");

            await Step($"Teleporting to {city}", async () =>
            {
                var before = clientState.TerritoryType;
                if (!travel.Execute(db.LocalizePlaceName(city))) throw new AutoPilotException("the teleport did not start");
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
            await ChooseMenu(db.LocalizeMenuText(S.GcSupplyMenuText), ct).ConfigureAwait(false);
            await WaitUntil(() => GameUi.IsVisible("GrandCompanySupplyList"), StepTimeout, "the supply window", ct).ConfigureAwait(false);
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

        var queue = coordinator.BuildQueueFromPlan(r => r.Checked);
        if (queue.Count == 0) return;
        await Step($"Cleaning {queue.Count} more item{(queue.Count == 1 ? "" : "s")} found in {what}", () => Execute(queue), ct).ConfigureAwait(false);
    }

    // ---------- movement & interaction ----------

    private async Task WalkToAndInteractAsync(string objectName, string expectAddon, CancellationToken ct, bool orMenu = false)
    {
        var target = await OnFramework(() => FindNearest(objectName)).ConfigureAwait(false);
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
                    await WaitUntil(() => GameUi.IsVisible(expectAddon) || (orMenu && GameUi.SelectStringReady()), TimeSpan.FromSeconds(6), $"the {objectName.ToLowerInvariant()} to respond", ct).ConfigureAwait(false);
                    return;
                }
                catch (AutoPilotException) when (attempt < 2) { }
            }
            throw new AutoPilotException($"the {objectName.ToLowerInvariant()} did not respond; close any retainer, shop or dresser window and run again");
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

    private async Task ChooseMenu(string text, CancellationToken ct)
    {
        IReadOnlyList<string> entries = Array.Empty<string>();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            entries = await framework.RunOnFrameworkThread(GameUi.SelectStringEntries).ConfigureAwait(false);
            if (entries.Count > 0) break;
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
        var chosen = await framework.RunOnFrameworkThread(() => GameUi.SelectStringChoose(text)).ConfigureAwait(false);
        if (chosen < 0)
            throw new AutoPilotException(entries.Count == 0
                ? $"the menu stayed empty, so '{text}' could not be chosen"
                : $"the menu had no '{text}' option");
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
        chat.PrintError($"Stopped: {message}.", "Tidy Up");
    }

    /// <summary>Nothing to do: not an error, one quiet line.</summary>
    private void Nothing(string message)
    {
        Status = message;
        chat.Print($"{message}.", "Tidy Up");
    }

    private sealed class AutoPilotException(string message) : Exception(message);
}
