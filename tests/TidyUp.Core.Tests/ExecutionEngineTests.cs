using TidyUp.Core.Execution;
using TidyUp.Core.Logging;
using TidyUp.Core.Model;
using static TidyUp.Core.Tests.TestData;

namespace TidyUp.Core.Tests;

/// <summary>An in-memory game: containers hold items, actions mutate them, and every call is recorded.</summary>
internal sealed class FakeGame : IGameActions
{
    public Dictionary<SlotRef, ScannedItem> Slots { get; } = new();
    public HashSet<ContainerKind> Open { get; } = new() { ContainerKind.Inventory, ContainerKind.Armoury };
    public HashSet<ActionKind> AvailableActions { get; } = new() { ActionKind.Discard, ActionKind.VendorSell, ActionKind.ExpertDelivery, ActionKind.Desynth };
    public List<string> Calls { get; } = new();
    public int FreeSlots { get; set; } = 10;
    public Func<SlotRef, bool> FailWhen { get; set; } = _ => false;
    public Func<Task>? BeforeAction { get; set; }
    public bool MateriaFails { get; set; }
    public string? LastFailure { get; private set; }

    public bool IsContainerAvailable(ContainerKind kind, ulong ownerId) => Open.Contains(kind);
    public ScannedItem? ReadSlot(SlotRef slot) => Slots.GetValueOrDefault(slot);
    public bool CanRetrieveMateriaIn(ContainerKind kind) => kind is ContainerKind.Inventory or ContainerKind.Armoury;

    public Task<SlotRef?> MoveToInventoryAsync(SlotRef slot, uint itemId, int quantity, bool isHq, CancellationToken ct)
    {
        Calls.Add($"move:{slot}");
        if (!Slots.TryGetValue(slot, out var item)) return Task.FromResult<SlotRef?>(null);
        var landing = new SlotRef(ContainerKind.Inventory, 0, 90 + Slots.Count);
        Slots.Remove(slot);
        Slots[landing] = item with { Slot = landing };
        return Task.FromResult<SlotRef?>(landing);
    }

    public SlotRef? FindSlot(ContainerKind kind, ulong ownerId, uint itemId, int quantity, bool isHq, IReadOnlySet<SlotRef> exclude, SlotRef preferred)
    {
        var hits = Slots.Values.Where(i => i.Slot.Kind == kind && (kind != ContainerKind.Retainer || i.Slot.OwnerId == ownerId)
                                          && i.ItemId == itemId && i.Quantity == quantity && i.IsHq == isHq && !exclude.Contains(i.Slot)).ToList();
        if (hits.Count == 0) return null;
        return (hits.FirstOrDefault(h => h.Slot == preferred) ?? hits[0]).Slot;
    }
    public int FreeInventorySlots() => FreeSlots;
    public bool IsActionAvailable(ActionKind action) => AvailableActions.Contains(action);
    public string ActionRequirement(ActionKind action) => $"open a window for {action}";

    private async Task<bool> Do(string name, SlotRef slot)
    {
        if (BeforeAction is not null) await BeforeAction();
        Calls.Add($"{name}:{slot}");
        if (FailWhen(slot)) return false;
        Slots.Remove(slot);
        return true;
    }

    public Task<bool> DiscardAsync(SlotRef slot, uint itemId, CancellationToken ct) => Do("discard", slot);
    public Task<bool> VendorSellAsync(SlotRef slot, uint itemId, CancellationToken ct) => Do("sell", slot);
    public int MarketSlots { get; set; } = 20;
    public Task<bool> MarketListAsync(SlotRef slot, uint itemId, long unitPrice, int quantity, CancellationToken ct)
    {
        MarketSlots--;
        if (Slots.TryGetValue(slot, out var item) && quantity < item.Quantity && !FailWhen(slot))
        {
            Calls.Add($"list@{unitPrice}x{quantity}:{slot}");
            Slots[slot] = item with { Quantity = item.Quantity - quantity };
            return Task.FromResult(true);
        }
        return Do($"list@{unitPrice}", slot);
    }
    public int FreeMarketSlots() => MarketSlots;
    public Task<bool> ExpertDeliveryAsync(SlotRef slot, uint itemId, CancellationToken ct) => Do("seals", slot);
    public Task<bool> DesynthAsync(SlotRef slot, uint itemId, CancellationToken ct) => Do("desynth", slot);

    public Task<bool> RetrieveMateriaAsync(SlotRef slot, uint itemId, CancellationToken ct)
    {
        Calls.Add($"materia:{slot}");
        if (MateriaFails) { LastFailure = "no Retrieve Materia entry"; return Task.FromResult(false); }
        if (Slots.TryGetValue(slot, out var item)) Slots[slot] = item with { Materia = Array.Empty<ushort>() };
        return Task.FromResult(true);
    }

    public Task<SlotRef?> RestoreFromDresserAsync(SlotRef dresserSlot, uint itemId, CancellationToken ct)
    {
        Calls.Add($"restore:{dresserSlot}");
        if (!Slots.TryGetValue(dresserSlot, out var item)) return Task.FromResult<SlotRef?>(null);
        var landed = new SlotRef(ContainerKind.Inventory, 0, 99);
        Slots.Remove(dresserSlot);
        Slots[landed] = item with { Slot = landed };
        FreeSlots--;
        return Task.FromResult<SlotRef?>(landed);
    }
}

public class ExecutionEngineTests
{
    private static readonly RunIdentity Who = new(0xC0FFEE, "Test Char");

    private static QueuedAction Q(SlotRef slot, uint itemId, int qty, ActionKind action = ActionKind.Discard, bool materia = false, bool hq = false) =>
        new(slot, itemId, qty, hq, action, materia, $"item{itemId}", 0, "rule");

    [Fact]
    public async Task Executes_exactly_what_was_shown_and_logs_each_success()
    {
        var game = new FakeGame();
        game.Slots[Inv(0)] = ScannedItem.Simple(Inv(0), 1, 14);
        game.Slots[Inv(1)] = ScannedItem.Simple(Inv(1), 3, 9);
        var log = new MemoryRunLog();

        var report = await new ExecutionEngine(game, log, new NoDelay())
            .ExecuteAsync([Q(Inv(0), 1, 14, ActionKind.VendorSell), Q(Inv(1), 3, 9)], Who, CancellationToken.None);

        Assert.Equal(2, report.Done);
        Assert.False(report.Aborted);
        Assert.Equal(["sell:Inventory:0#0", "discard:Inventory:0#1"], game.Calls);
        Assert.Equal(2, log.Entries.Count);
        Assert.Equal(ActionKind.VendorSell, log.Entries[0].Action);
        Assert.Empty(game.Slots);
    }

    [Fact]
    public async Task Skips_items_that_are_no_longer_anywhere_in_the_container()
    {
        var game = new FakeGame();
        game.Slots[Inv(0)] = ScannedItem.Simple(Inv(0), 1, 13);      // quantity differs, and no other 1×14 exists
        game.Slots[Inv(1)] = ScannedItem.Simple(Inv(1), 2, 1);       // item differs
        game.Slots[Inv(2)] = ScannedItem.Simple(Inv(2), 1, 1, hq: true); // only an HQ copy exists
        // Inv(3) is empty now

        var report = await new ExecutionEngine(game, new MemoryRunLog(), new NoDelay())
            .ExecuteAsync([Q(Inv(0), 1, 14), Q(Inv(1), 5, 1), Q(Inv(2), 1, 1), Q(Inv(3), 7, 1)], Who, CancellationToken.None);

        Assert.Equal(0, report.Done);
        Assert.Equal(4, report.Skipped);
        Assert.Empty(game.Calls);
        Assert.Equal(3, game.Slots.Count); // nothing touched
    }

    [Fact]
    public async Task Finds_the_planned_item_by_identity_when_the_cached_slot_numbering_is_wrong()
    {
        // The plan says slot 1 holds a 1×14 stack; live, that stack sits in slot 9 and slot 1 holds something else.
        var game = new FakeGame();
        game.Open.Add(ContainerKind.Retainer);
        game.Slots[Ret(1)] = ScannedItem.Simple(Ret(1), 2, 1);
        game.Slots[Ret(9)] = ScannedItem.Simple(Ret(9), 1, 14);
        game.Slots[Ret(10)] = ScannedItem.Simple(Ret(10), 1, 14); // an identical second stack

        var report = await new ExecutionEngine(game, new MemoryRunLog(), new NoDelay())
            .ExecuteAsync([Q(Ret(1), 1, 14), Q(Ret(2), 1, 14)], Who, CancellationToken.None);

        Assert.Equal(2, report.Done);
        Assert.Equal(0, report.Skipped);
        Assert.Equal(2, game.Calls.Count);
        Assert.True(game.Slots.ContainsKey(Ret(1)));        // the mismatched slot was never touched
        Assert.False(game.Slots.ContainsKey(Ret(9)));       // both identical stacks were used, each once
        Assert.False(game.Slots.ContainsKey(Ret(10)));
    }

    [Fact]
    public async Task Closed_containers_stay_pending_and_do_not_block_open_ones()
    {
        var game = new FakeGame();
        game.Slots[Inv(0)] = ScannedItem.Simple(Inv(0), 1, 1);
        game.Slots[Saddle(0)] = ScannedItem.Simple(Saddle(0), 1, 1);
        game.Slots[Ret(0)] = ScannedItem.Simple(Ret(0), 1, 1);

        var report = await new ExecutionEngine(game, new MemoryRunLog(), new NoDelay())
            .ExecuteAsync([Q(Saddle(0), 1, 1), Q(Ret(0), 1, 1), Q(Inv(0), 1, 1)], Who, CancellationToken.None);

        Assert.Equal(1, report.Done);
        Assert.Equal(2, report.Pending.Count);
        Assert.Equal(1, report.PendingByContainer[ContainerKind.Saddlebag]);
        Assert.Equal(1, report.PendingByContainer[ContainerKind.Retainer]);
        Assert.Equal(["discard:Inventory:0#0"], game.Calls);
    }

    [Fact]
    public async Task Dresser_items_restore_first_then_discard_from_the_landing_slot_and_run_last()
    {
        var game = new FakeGame();
        game.Open.Add(ContainerKind.GlamourDresser);
        game.Slots[SlotRef.Dresser(5)] = ScannedItem.Simple(SlotRef.Dresser(5), 6, 1);
        game.Slots[Inv(0)] = ScannedItem.Simple(Inv(0), 1, 1);

        var report = await new ExecutionEngine(game, new MemoryRunLog(), new NoDelay())
            .ExecuteAsync([Q(SlotRef.Dresser(5), 6, 1), Q(Inv(0), 1, 1)], Who, CancellationToken.None);

        Assert.Equal(2, report.Done);
        Assert.Equal(["discard:Inventory:0#0", "restore:GlamourDresser:4294901761#5", "discard:Inventory:0#99"], game.Calls);
    }

    [Fact]
    public async Task Dresser_restore_waits_for_a_free_inventory_slot()
    {
        var game = new FakeGame { FreeSlots = 0 };
        game.Open.Add(ContainerKind.GlamourDresser);
        game.Slots[SlotRef.Dresser(5)] = ScannedItem.Simple(SlotRef.Dresser(5), 6, 1);

        var report = await new ExecutionEngine(game, new MemoryRunLog(), new NoDelay())
            .ExecuteAsync([Q(SlotRef.Dresser(5), 6, 1)], Who, CancellationToken.None);

        Assert.Equal(0, report.Done);
        Assert.Empty(game.Calls);
        Assert.Single(report.Pending);
    }

    [Fact]
    public async Task Materia_is_retrieved_before_the_destructive_action()
    {
        var game = new FakeGame();
        game.Slots[Arm(0)] = WithMateria(ScannedItem.Simple(Arm(0), 4, 1), 12, 34);

        var report = await new ExecutionEngine(game, new MemoryRunLog(), new NoDelay())
            .ExecuteAsync([Q(Arm(0), 4, 1, ActionKind.ExpertDelivery, materia: true)], Who, CancellationToken.None);

        Assert.Equal(1, report.Done);
        Assert.Equal(["materia:Armoury:3202#0", "seals:Armoury:3202#0"], game.Calls);
    }

    [Fact]
    public async Task Failed_materia_retrieval_parks_the_item_instead_of_counting_as_a_failure()
    {
        var game = new FakeGame { MateriaFails = true };
        for (var i = 0; i < 4; i++) game.Slots[Arm(i)] = WithMateria(ScannedItem.Simple(Arm(i), 4, 1), 12);

        var report = await new ExecutionEngine(game, new MemoryRunLog(), new NoDelay())
            .ExecuteAsync(Enumerable.Range(0, 4).Select(i => Q(Arm(i), 4, 1, ActionKind.Discard, materia: true)).ToList(), Who, CancellationToken.None);

        Assert.False(report.Aborted);
        Assert.Equal(0, report.Done);
        Assert.Equal(0, report.Failed);
        Assert.Equal(4, report.Pending.Count);
        Assert.All(report.Pending, p => Assert.Contains("no Retrieve Materia entry", report.PendingReasons[p]));
        Assert.Equal(4, game.Slots.Count);
    }

    [Fact]
    public async Task Materia_failure_can_be_told_to_act_anyway()
    {
        var game = new FakeGame { MateriaFails = true };
        game.Slots[Arm(0)] = WithMateria(ScannedItem.Simple(Arm(0), 4, 1), 12);

        var report = await new ExecutionEngine(game, new MemoryRunLog(), new NoDelay(), new ExecutionOptions { OnMateriaFailure = MateriaFailurePolicy.ActAnyway })
            .ExecuteAsync([Q(Arm(0), 4, 1, ActionKind.Discard, materia: true)], Who, CancellationToken.None);

        Assert.Equal(1, report.Done);
        Assert.Empty(game.Slots);
    }

    [Fact]
    public async Task Materia_found_on_the_live_item_is_retrieved_even_when_the_plan_missed_it()
    {
        var game = new FakeGame();
        game.Slots[Arm(0)] = WithMateria(ScannedItem.Simple(Arm(0), 4, 1), 12);

        var report = await new ExecutionEngine(game, new MemoryRunLog(), new NoDelay())
            .ExecuteAsync([Q(Arm(0), 4, 1, ActionKind.Discard, materia: false)], Who, CancellationToken.None);

        Assert.Equal(1, report.Done);
        Assert.Contains(game.Calls, c => c.StartsWith("materia:"));
        Assert.True(game.Calls.IndexOf(game.Calls.First(c => c.StartsWith("materia:"))) < game.Calls.IndexOf(game.Calls.First(c => c.StartsWith("discard:"))));
    }

    [Fact]
    public async Task Slotted_retainer_items_are_brought_home_and_finished_there()
    {
        var game = new FakeGame();
        game.Open.Add(ContainerKind.Retainer);
        game.Slots[Ret(3)] = WithMateria(ScannedItem.Simple(Ret(3), 4, 1), 12, 34);

        var first = await new ExecutionEngine(game, new MemoryRunLog(), new NoDelay())
            .ExecuteAsync([Q(Ret(3), 4, 1, ActionKind.Discard, materia: true)], Who, CancellationToken.None);

        Assert.Equal(0, first.Done);
        Assert.Empty(first.Pending);
        var follow = Assert.Single(first.Moved);
        Assert.Equal(ContainerKind.Inventory, follow.Kind);
        Assert.DoesNotContain(game.Calls, c => c.StartsWith("materia:") || c.StartsWith("discard:"));
        Assert.Contains("brought back to your bags", first.Summary());

        var second = await new ExecutionEngine(game, new MemoryRunLog(), new NoDelay())
            .ExecuteAsync([follow], Who, CancellationToken.None);

        Assert.Equal(1, second.Done);
        Assert.Contains(game.Calls, c => c == $"materia:{follow.Slot}");
        Assert.Empty(game.Slots);
    }

    [Fact]
    public async Task Split_listings_go_up_in_pieces_and_the_rest_waits_when_slots_run_out()
    {
        var game = new FakeGame { MarketSlots = 2 };
        game.AvailableActions.Add(ActionKind.MarketList);
        game.Slots[Inv(0)] = ScannedItem.Simple(Inv(0), 12, 5);
        var log = new MemoryRunLog();

        var report = await new ExecutionEngine(game, log, new NoDelay(), new ExecutionOptions { MarketStackSize = 2 })
            .ExecuteAsync([Q(Inv(0), 12, 5, ActionKind.MarketList) with { UnitPrice = 400 }], Who, CancellationToken.None);

        Assert.Equal([$"list@400x2:{Inv(0)}", $"list@400x2:{Inv(0)}"], game.Calls);
        Assert.Equal(0, report.Done);
        var rest = Assert.Single(report.Moved);
        Assert.Equal(1, rest.Quantity);
        Assert.False(rest.BroughtHome);
        Assert.Equal(1, game.Slots[Inv(0)].Quantity);
        Assert.Equal(4, Assert.Single(log.Entries).Quantity);
        Assert.Contains("partly listed", report.Summary());
    }

    [Fact]
    public async Task Split_listings_finish_as_done_when_every_piece_goes_up()
    {
        var game = new FakeGame { MarketSlots = 5 };
        game.AvailableActions.Add(ActionKind.MarketList);
        game.Slots[Inv(0)] = ScannedItem.Simple(Inv(0), 12, 5);

        var report = await new ExecutionEngine(game, new MemoryRunLog(), new NoDelay(), new ExecutionOptions { MarketStackSize = 2 })
            .ExecuteAsync([Q(Inv(0), 12, 5, ActionKind.MarketList) with { UnitPrice = 400 }], Who, CancellationToken.None);

        Assert.Equal(1, report.Done);
        Assert.Equal(3, game.Calls.Count);
        Assert.Empty(game.Slots);
    }

    [Fact]
    public async Task Market_listing_needs_the_sell_list_a_price_and_a_free_slot()
    {
        var game = new FakeGame { MarketSlots = 1 };
        game.AvailableActions.Add(ActionKind.MarketList);
        game.Slots[Inv(0)] = ScannedItem.Simple(Inv(0), 12, 3);
        game.Slots[Inv(1)] = ScannedItem.Simple(Inv(1), 12, 2);
        game.Slots[Inv(2)] = ScannedItem.Simple(Inv(2), 6, 1);

        var queue = new List<QueuedAction>
        {
            Q(Inv(0), 12, 3, ActionKind.MarketList) with { UnitPrice = 400 },
            Q(Inv(1), 12, 2, ActionKind.MarketList) with { UnitPrice = 400 },
            Q(Inv(2), 6, 1, ActionKind.MarketList), // no price
        };
        var report = await new ExecutionEngine(game, new MemoryRunLog(), new NoDelay()).ExecuteAsync(queue, Who, CancellationToken.None);

        Assert.Equal(1, report.Done);
        Assert.Contains(game.Calls, c => c == $"list@400:{Inv(0)}");
        Assert.Equal(2, report.Pending.Count);
        Assert.Contains(report.PendingReasons.Values, r => r.Contains("market slots are full"));
        Assert.Contains(report.PendingReasons.Values, r => r.Contains("no market price"));
    }

    [Fact]
    public async Task Retainer_items_sell_in_place_and_expert_delivery_items_come_home_first()
    {
        var game = new FakeGame();
        game.Open.Add(ContainerKind.Retainer);
        game.AvailableActions.Add(ActionKind.VendorSell); // the retainer's inventory is open: it buys
        game.Slots[Ret(2)] = ScannedItem.Simple(Ret(2), 1, 5);
        game.Slots[Ret(3)] = ScannedItem.Simple(Ret(3), 4, 1);

        var report = await new ExecutionEngine(game, new MemoryRunLog(), new NoDelay())
            .ExecuteAsync([Q(Ret(2), 1, 5, ActionKind.VendorSell), Q(Ret(3), 4, 1, ActionKind.ExpertDelivery)], Who, CancellationToken.None);

        Assert.Equal(1, report.Done);
        Assert.Contains(game.Calls, c => c == $"sell:{Ret(2)}");
        var follow = Assert.Single(report.Moved);
        Assert.True(follow.BroughtHome);
        Assert.Equal(ActionKind.ExpertDelivery, follow.Action);
        Assert.Equal(ContainerKind.Inventory, follow.Kind);
    }

    [Fact]
    public async Task Materia_retrieval_needs_free_slots_per_materia()
    {
        var game = new FakeGame { FreeSlots = 1 };
        game.Slots[Arm(0)] = WithMateria(ScannedItem.Simple(Arm(0), 4, 1), 12, 34);

        var report = await new ExecutionEngine(game, new MemoryRunLog(), new NoDelay())
            .ExecuteAsync([Q(Arm(0), 4, 1, ActionKind.Discard, materia: true)], Who, CancellationToken.None);

        Assert.Single(report.Pending);
        Assert.Empty(game.Calls);
    }

    [Fact]
    public async Task A_single_failure_is_skipped_and_the_run_continues()
    {
        var game = new FakeGame { FailWhen = s => s.Slot == 1 };
        for (var i = 0; i < 4; i++) game.Slots[Inv(i)] = ScannedItem.Simple(Inv(i), 1, 1);

        var report = await new ExecutionEngine(game, new MemoryRunLog(), new NoDelay())
            .ExecuteAsync(Enumerable.Range(0, 4).Select(i => Q(Inv(i), 1, 1)).ToList(), Who, CancellationToken.None);

        Assert.False(report.Aborted);
        Assert.Equal(3, report.Done);
        Assert.Equal(1, report.Failed);
        Assert.Empty(report.Pending);
        Assert.Equal(4, game.Calls.Count);
    }

    [Fact]
    public async Task Three_failures_in_a_row_abort_the_rest()
    {
        var game = new FakeGame { FailWhen = s => s.Slot is 1 or 2 or 3 };
        for (var i = 0; i < 6; i++) game.Slots[Inv(i)] = ScannedItem.Simple(Inv(i), 1, 1);

        var report = await new ExecutionEngine(game, new MemoryRunLog(), new NoDelay())
            .ExecuteAsync(Enumerable.Range(0, 6).Select(i => Q(Inv(i), 1, 1)).ToList(), Who, CancellationToken.None);

        Assert.True(report.Aborted);
        Assert.Equal(1, report.Done);
        Assert.Equal(3, report.Failed);
        Assert.Equal(2, report.Pending.Count);
        Assert.Contains("3 items failed in a row", report.AbortReason);
        Assert.All(report.Pending, p => Assert.Contains("earlier failure", report.PendingReasons[p]));
    }

    [Fact]
    public async Task Actions_needing_a_closed_npc_window_stay_pending()
    {
        var game = new FakeGame();
        game.AvailableActions.Remove(ActionKind.VendorSell);
        game.Slots[Inv(0)] = ScannedItem.Simple(Inv(0), 1, 1);

        var report = await new ExecutionEngine(game, new MemoryRunLog(), new NoDelay())
            .ExecuteAsync([Q(Inv(0), 1, 1, ActionKind.VendorSell)], Who, CancellationToken.None);

        Assert.Single(report.Pending);
        Assert.Empty(game.Calls);
    }

    [Fact]
    public async Task Cancellation_stops_before_the_next_action()
    {
        var game = new FakeGame();
        for (var i = 0; i < 3; i++) game.Slots[Inv(i)] = ScannedItem.Simple(Inv(i), 1, 1);
        var cts = new CancellationTokenSource();
        game.BeforeAction = () => { cts.Cancel(); return Task.CompletedTask; };

        var report = await new ExecutionEngine(game, new MemoryRunLog(), new NoDelay())
            .ExecuteAsync(Enumerable.Range(0, 3).Select(i => Q(Inv(i), 1, 1)).ToList(), Who, cts.Token);

        Assert.Equal(1, report.Done);
        Assert.Equal(2, report.Pending.Count);
    }

    [Fact]
    public async Task Progress_reports_pending_and_terminal_results()
    {
        var game = new FakeGame();
        game.Slots[Inv(0)] = ScannedItem.Simple(Inv(0), 1, 1);
        var seen = new List<ActionOutcome>();
        var progress = new Progress<ActionResult>(r => seen.Add(r.Outcome));

        await new ExecutionEngine(game, new MemoryRunLog(), new NoDelay())
            .ExecuteAsync([Q(Inv(0), 1, 1), Q(Saddle(0), 1, 1)], Who, CancellationToken.None, new SyncProgress(seen));

        Assert.Contains(ActionOutcome.Done, seen);
        Assert.Contains(ActionOutcome.Pending, seen);
    }

    private sealed class SyncProgress(List<ActionOutcome> sink) : IProgress<ActionResult>
    {
        public void Report(ActionResult value) => sink.Add(value.Outcome);
    }
}
