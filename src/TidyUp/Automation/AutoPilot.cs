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
public sealed class AutoPilot
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
    public string Status { get; private set; } = string.Empty;
    public string? LastError { get; private set; }

    /// <summary>Set by the plugin so the pilot can tell whether the review window is still open while it waits.</summary>
    public Func<bool> IsReviewOpen { get; set; } = () => false;

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
        if (S.TravelToInn && !travel.IsInstalled) return "Lifestream is not installed";
        if (!nav.IsInstalled) return "vnavmesh is not installed";
        return null;
    }

    public void Stop()
    {
        cts?.Cancel();
        nav.Stop();
        travel.Abort();
        coordinator.CancelRun();
    }

    /// <summary>Runs the whole accepted plan, travelling as needed. Returns when done, stopped, or failed.</summary>
    public async Task RunAsync()
    {
        if (IsRunning || coordinator.CurrentPlan is null) return;
        if (MissingDependency() is { } missing) { Fail(missing); return; }
        if (condition[ConditionFlag.InCombat] || condition[ConditionFlag.BoundByDuty]) { Fail("Not while in combat or in a duty"); return; }

        var queue = coordinator.BuildQueueFromPlan(r => true);
        if (queue.Count == 0) { Fail("Nothing selected"); return; }

        IsRunning = true;
        LastError = null;
        cts?.Dispose();
        cts = new CancellationTokenSource();
        var ct = cts.Token;
        try
        {
            var here = queue.Where(q => q.Kind.IsAlwaysLoaded() && q.Action is not ActionKind.VendorSell and not ActionKind.ExpertDelivery).ToList();
            var sells = queue.Where(q => q.Kind.IsAlwaysLoaded() && q.Action == ActionKind.VendorSell).ToList();
            var seals = queue.Where(q => q.Kind.IsAlwaysLoaded() && q.Action == ActionKind.ExpertDelivery).ToList();
            var saddle = queue.Where(q => q.Kind == ContainerKind.Saddlebag).ToList();
            var retainers = queue.Where(q => q.Kind == ContainerKind.Retainer).GroupBy(q => q.Slot.OwnerId).ToDictionary(g => g.Key, g => g.ToList());
            var dresser = queue.Where(q => q.Kind == ContainerKind.GlamourDresser).ToList();

            coordinator.SuppressChatSummary = true;
            tally.Clear();

            if (here.Count > 0) await Step("Cleaning inventory and armoury", () => Execute(here), ct);

            if (S.OpenSaddlebag && saddle.Count > 0) await SaddlebagAsync(saddle, ct);

            var needsInn = (S.VisitRetainers && retainers.Count > 0) || (S.VisitDresser && dresser.Count > 0);
            if (needsInn)
            {
                await TravelToInnAsync(ct);
                if (S.VisitRetainers) await RetainersAsync(retainers, ct);
                if (S.VisitDresser) await DresserAsync(dresser, ct);
            }

            if (S.VisitGrandCompany && seals.Count > 0) await GrandCompanyAsync(seals, ct);
            if (S.SellAtVendor && sells.Count > 0) await VendorAsync(sells, ct);

            Status = "Done";
            chat.Print($"Tidy Up: hands-free run finished. {tally.Summary()}", "Tidy Up");
            foreach (var line in tally.PendingLines()) chat.Print($"  {line}", "Tidy Up");
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped";
            chat.Print("Tidy Up: stopped. Nothing after the current item was touched.", "Tidy Up");
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

        public void Clear() { Done = Skipped = Failed = 0; Pending.Clear(); }

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
            if (Skipped > 0) parts.Add($"{Skipped} skipped because they changed");
            if (Failed > 0) parts.Add($"{Failed} failed");
            var pending = Pending.Values.Sum();
            if (pending > 0) parts.Add($"{pending} still waiting");
            return string.Join(", ", parts) + ".";
        }

        public IEnumerable<string> PendingLines() =>
            Pending.Where(kv => kv.Value > 0).Select(kv => $"{kv.Value} waiting: {(string.IsNullOrEmpty(kv.Key) ? "container not open" : kv.Key)}.");
    }

    private async Task Execute(IReadOnlyList<QueuedAction> rows)
    {
        await coordinator.ExecuteQueueAsync(rows, refreshAfter: false).ConfigureAwait(false);
        tally.Add(coordinator.LastReport);
    }

    // ---------- steps ----------

    private async Task SaddlebagAsync(List<QueuedAction> rows, CancellationToken ct)
    {
        await Step("Opening the saddlebag", async () =>
        {
            var id = saddlebagCommandId ??= db.MainCommandIdForEnglishName(S.SaddlebagCommandName);
            if (id is null) throw new AutoPilotException($"No main command named '{S.SaddlebagCommandName}'");
            await framework.RunOnFrameworkThread(() => GameUi.ExecuteMainCommand(id.Value)).ConfigureAwait(false);
            await WaitUntil(() => GameInventoryScanner.IsSaddlebagLoaded() && GameUi.IsVisible("InventoryBuddy"), StepTimeout, "the saddlebag to open", ct).ConfigureAwait(false);
        }, ct);
        await Step("Cleaning the saddlebag", () => Execute(rows), ct);
        await PauseForUnseen(ContainerKind.Saddlebag, ct).ConfigureAwait(false);
        await framework.RunOnFrameworkThread(() => GameUi.Close("InventoryBuddy")).ConfigureAwait(false);
    }

    private async Task TravelToInnAsync(CancellationToken ct)
    {
        if (await OnFramework(IsInInn).ConfigureAwait(false)) return;
        if (!S.TravelToInn) throw new AutoPilotException("Not in an inn and travel is turned off");
        await Step("Travelling to an inn", async () =>
        {
            if (!travel.GoToInn(S.InnIndex)) throw new AutoPilotException("Lifestream refused the inn shortcut");
            await Task.Delay(1500, ct).ConfigureAwait(false);
            await WaitUntil(() => !travel.IsBusy && !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51] && IsInInn(),
                TimeSpan.FromSeconds(S.TravelTimeoutSeconds), "the inn room", ct).ConfigureAwait(false);
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }, ct);
    }

    private async Task RetainersAsync(Dictionary<ulong, List<QueuedAction>> byRetainer, CancellationToken ct)
    {
        if (byRetainer.Count == 0) return;
        await WalkToAndInteractAsync(S.BellObjectName, "RetainerList", ct).ConfigureAwait(false);

        // The list appears before the server has filled it; selecting too early is silently ignored.
        await WaitUntil(RetainerListReady, StepTimeout, "the retainer list to fill", ct).ConfigureAwait(false);
        await Task.Delay(1200, ct).ConfigureAwait(false);

        var order = await OnFramework(RetainerOrder).ConfigureAwait(false);
        for (var index = 0; index < order.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var (id, name) = order[index];
            var rows = byRetainer.GetValueOrDefault(id) ?? new List<QueuedAction>();
            if (rows.Count == 0 && !S.PauseForUnseenRows) continue;

            await Step($"Opening {name}", async () =>
            {
                var i = index;
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    await framework.RunOnFrameworkThread(() => GameUi.RetainerListSelect(S.RetainerListSelect, i)).ConfigureAwait(false);
                    try
                    {
                        await WaitUntil(() => GameUi.SelectStringReady(), TimeSpan.FromSeconds(6), $"{name}'s menu", ct).ConfigureAwait(false);
                        return;
                    }
                    catch (AutoPilotException) when (attempt < 2)
                    {
                        await Task.Delay(800, ct).ConfigureAwait(false);
                    }
                }
                throw new AutoPilotException($"{name}'s menu did not open after selecting row {i} three times. Try another 'Retainer list callback' value in Settings › Automation.");
            }, ct);

            if (rows.Count > 0 || S.PauseForUnseenRows)
            {
                await Step($"Opening {name}'s inventory", async () =>
                {
                    await ChooseMenu(S.EntrustMenuText, ct).ConfigureAwait(false);
                    await WaitUntil(() => GameUi.AnyVisible("InventoryRetainer", "InventoryRetainerLarge") && GameInventoryScanner.IsRetainerOpen(id),
                        StepTimeout, $"{name}'s inventory", ct).ConfigureAwait(false);
                    await Task.Delay(600, ct).ConfigureAwait(false);
                }, ct);
                if (rows.Count > 0) await Step($"Cleaning {name}", () => Execute(rows), ct);
                await PauseForUnseen(ContainerKind.Retainer, ct).ConfigureAwait(false);
                await framework.RunOnFrameworkThread(() => { GameUi.Close("InventoryRetainer"); GameUi.Close("InventoryRetainerLarge"); }).ConfigureAwait(false);
                await WaitUntil(() => GameUi.SelectStringReady(), StepTimeout, $"{name}'s menu", ct).ConfigureAwait(false);
            }

            await Step($"Leaving {name}", async () =>
            {
                await ChooseMenu(S.QuitMenuText, ct).ConfigureAwait(false);
                await WaitUntil(() => GameUi.IsVisible("RetainerList") && !GameUi.SelectStringReady(), StepTimeout, "the retainer list", ct).ConfigureAwait(false);
                await Task.Delay(500, ct).ConfigureAwait(false);
            }, ct);
        }

        await framework.RunOnFrameworkThread(() => GameUi.Close("RetainerList")).ConfigureAwait(false);
    }

    private async Task DresserAsync(List<QueuedAction> rows, CancellationToken ct)
    {
        if (rows.Count == 0 && !S.PauseForUnseenRows) return;
        await WalkToAndInteractAsync(S.DresserObjectName, "MiragePrismPrismBox", ct).ConfigureAwait(false);
        await WaitUntil(GameInventoryScanner.IsDresserLoaded, StepTimeout, "the dresser to load", ct).ConfigureAwait(false);
        await Task.Delay(800, ct).ConfigureAwait(false);
        if (rows.Count > 0) await Step("Cleaning the glamour dresser", () => Execute(rows), ct);
        await PauseForUnseen(ContainerKind.GlamourDresser, ct).ConfigureAwait(false);
        await framework.RunOnFrameworkThread(() => GameUi.Close("MiragePrismPrismBox")).ConfigureAwait(false);
    }

    /// <summary>Selling needs a real merchant: find the named NPC nearby, open its shop, sell from the bags.</summary>
    private async Task VendorAsync(List<QueuedAction> sells, CancellationToken ct)
    {
        var npc = await OnFramework(() => FindNearest(S.VendorNpcName)).ConfigureAwait(false);
        if (npc is null)
        {
            var reason = $"no '{S.VendorNpcName}' within reach; sell them at any merchant";
            tally.Pending[reason] = tally.Pending.GetValueOrDefault(reason) + sells.Count;
            return;
        }
        await WalkToAndInteractAsync(S.VendorNpcName, "Shop", ct, orMenu: true).ConfigureAwait(false);
        if (!await OnFramework(() => GameUi.IsVisible("Shop")).ConfigureAwait(false))
        {
            await Step("Opening the shop", async () =>
            {
                await ChooseMenu(S.VendorMenuText, ct).ConfigureAwait(false);
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
        if (gc == 0) throw new AutoPilotException("No Grand Company on this character");

        var officer = await OnFramework(() => FindNearest(S.PersonnelOfficerName)).ConfigureAwait(false);
        if (officer is null)
        {
            if (!S.TravelToInn) throw new AutoPilotException($"No '{S.PersonnelOfficerName}' nearby and travel is off");
            if (!S.GcCityAetheryte.TryGetValue(gc, out var city) || string.IsNullOrEmpty(city))
                throw new AutoPilotException("No city aetheryte configured for your Grand Company");

            await Step($"Teleporting to {city}", async () =>
            {
                var before = clientState.TerritoryType;
                if (!travel.Execute(city)) throw new AutoPilotException("Lifestream refused the teleport");
                await Task.Delay(1500, ct).ConfigureAwait(false);
                await WaitUntil(() => !travel.IsBusy && !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51] && clientState.TerritoryType != before,
                    TimeSpan.FromSeconds(S.TravelTimeoutSeconds), city, ct).ConfigureAwait(false);
                await Task.Delay(1500, ct).ConfigureAwait(false);
            }, ct);

            if (S.GcAethernetShard.TryGetValue(gc, out var shard) && !string.IsNullOrEmpty(shard))
            {
                await Step($"Aethernet to {shard}", async () =>
                {
                    if (!travel.AethernetTeleport(shard)) throw new AutoPilotException($"Lifestream could not reach '{shard}'");
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                    await WaitUntil(() => !travel.IsBusy && !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51],
                        TimeSpan.FromSeconds(S.TravelTimeoutSeconds), shard, ct).ConfigureAwait(false);
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }, ct);
            }
        }

        await WalkToAndInteractAsync(S.PersonnelOfficerName, "SelectString", ct, orMenu: true).ConfigureAwait(false);
        await Step("Opening supply missions", async () =>
        {
            await ChooseMenu(S.GcSupplyMenuText, ct).ConfigureAwait(false);
            await WaitUntil(() => GameUi.IsVisible("GrandCompanySupplyList"), StepTimeout, "the supply window", ct).ConfigureAwait(false);
            await Task.Delay(800, ct).ConfigureAwait(false);
            var ints = S.ExpertDeliveryTabCallback.Split(',').Select(s => int.TryParse(s.Trim(), out var v) ? v : 0).ToList();
            await framework.RunOnFrameworkThread(() => GameUi.FireInts("GrandCompanySupplyList", ints)).ConfigureAwait(false);
            await Task.Delay(800, ct).ConfigureAwait(false);
        }, ct);
        await Step("Turning in for seals", () => Execute(seals), ct);
        await framework.RunOnFrameworkThread(() => GameUi.Close("GrandCompanySupplyList")).ConfigureAwait(false);
    }

    /// <summary>Re-plans the now-open container. If it shows rows the user never saw, opens the review and waits for them.</summary>
    private async Task PauseForUnseen(ContainerKind kind, CancellationToken ct)
    {
        if (!S.PauseForUnseenRows) return;
        await coordinator.RefreshPlanAsync(openWindow: false, focus: kind).ConfigureAwait(false);
        var plan = coordinator.CurrentPlan;
        if (plan is null || !plan.AllRows.Any(r => r.IsExecutable)) return;

        Status = $"Waiting for you to review the {kind.DisplayName().ToLowerInvariant()}";
        coordinator.RaiseOpenWindow();
        chat.Print($"Tidy Up: the {kind.DisplayName().ToLowerInvariant()} has items that were not in the accepted plan. Review them, then Clean or close the window to continue.", "Tidy Up");

        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        var sawRun = false;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (coordinator.IsRunning) sawRun = true;
            else if (sawRun || !IsReviewOpen()) break;
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
    }

    // ---------- movement & interaction ----------

    private async Task WalkToAndInteractAsync(string objectName, string expectAddon, CancellationToken ct, bool orMenu = false)
    {
        var target = await OnFramework(() => FindNearest(objectName)).ConfigureAwait(false);
        if (target is null)
        {
            var nearby = await OnFramework(NearbyObjectNames).ConfigureAwait(false);
            throw new AutoPilotException($"No object named '{objectName}' nearby. Set the name in Settings › Automation. Nearby: {string.Join(", ", nearby.Take(8))}");
        }

        await Step($"Walking to the {objectName.ToLowerInvariant()}", async () =>
        {
            var pos = target.Position;
            if (Distance(pos) > S.InteractRange)
            {
                if (!nav.IsReady) throw new AutoPilotException("vnavmesh has no navmesh for this zone yet");
                if (!nav.MoveCloseTo(pos, S.InteractRange - 0.5f)) throw new AutoPilotException("vnavmesh refused the path");
                await Task.Delay(500, ct).ConfigureAwait(false);
                await WaitUntil(() => !nav.IsMoving, TimeSpan.FromSeconds(60), $"arrival at the {objectName.ToLowerInvariant()}", ct).ConfigureAwait(false);
                if (Distance(pos) > S.InteractRange + 1.5f) throw new AutoPilotException($"Stopped {Distance(pos):0.0}y from the {objectName.ToLowerInvariant()}");
            }
        }, ct);

        await Step($"Using the {objectName.ToLowerInvariant()}", async () =>
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await framework.RunOnFrameworkThread(() => GameUi.Interact(target)).ConfigureAwait(false);
                try
                {
                    await WaitUntil(() => GameUi.IsVisible(expectAddon) || (orMenu && GameUi.SelectStringReady()), TimeSpan.FromSeconds(6), expectAddon, ct).ConfigureAwait(false);
                    return;
                }
                catch (AutoPilotException) when (attempt < 2) { }
            }
            throw new AutoPilotException($"The {objectName.ToLowerInvariant()} did not open {expectAddon}");
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

    /// <summary>Names of interactable objects within 30y, for the verification window when a lookup fails.</summary>
    public IReadOnlyList<string> NearbyObjectNames()
    {
        var me = objects.LocalPlayer?.Position ?? Vector3.Zero;
        return objects
            .Where(o => o.Address != 0 && o.ObjectKind is not Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc && Vector3.Distance(o.Position, me) < 30)
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
        return FindNearest(S.BellObjectName) is not null && FindNearest(S.DresserObjectName) is not null;
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
        return rm != null && rm->IsReady && rm->GetRetainerCount() > 0;
    }

    private static unsafe List<(ulong Id, string Name)> RetainerOrder()
    {
        var list = new List<(ulong, string)>();
        var rm = FFXIVClientStructs.FFXIV.Client.Game.RetainerManager.Instance();
        if (rm == null) return list;
        var count = rm->GetRetainerCount();
        for (uint i = 0; i < count; i++)
        {
            var r = rm->GetRetainerBySortedIndex(i);
            if (r == null || r->RetainerId == 0 || !r->Available) continue;
            list.Add((r->RetainerId, r->NameString));
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
                ? $"The menu never filled with entries, so '{text}' could not be chosen"
                : $"No menu entry containing '{text}'. Offered: {string.Join(" | ", entries)}");
        await Task.Delay(400, ct).ConfigureAwait(false);
    }

    // ---------- plumbing ----------

    private TimeSpan StepTimeout => TimeSpan.FromSeconds(S.StepTimeoutSeconds);

    private async Task Step(string status, Func<Task> body, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (condition[ConditionFlag.InCombat]) throw new AutoPilotException("Combat started");
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
        throw new AutoPilotException($"Timed out waiting for {what}");
    }

    private Task<T> OnFramework<T>(Func<T> f) => framework.RunOnFrameworkThread(f);

    private void Fail(string message)
    {
        LastError = message;
        Status = $"Stopped: {message}";
        log.Warning("AutoPilot: {Message}", message);
        chat.PrintError($"Tidy Up stopped: {message}", "Tidy Up");
    }

    private sealed class AutoPilotException(string message) : Exception(message);
}
