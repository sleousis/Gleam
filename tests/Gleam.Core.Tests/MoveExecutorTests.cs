using Gleam.Core.Execution;
using Gleam.Core.Logging;
using Gleam.Core.Model;
using Gleam.Core.Organizer.Capacity;
using Gleam.Core.Organizer.Execution;
using Gleam.Core.Organizer.Solving;
using static Gleam.Core.Tests.TestData;

namespace Gleam.Core.Tests;

/// <summary>In-memory storages: every kind has one page of the default size; moves relocate or merge stacks.</summary>
internal sealed class FakeMoveActions : IMoveActions
{
    public Dictionary<SlotRef, ScannedItem> Slots { get; } = new();
    public HashSet<StorageId> Open { get; } = new() { new(ContainerKind.Inventory), new(ContainerKind.Armoury) };
    public List<string> Calls { get; } = new();
    public Func<SlotRef, SlotRef, bool> Refuse { get; set; } = (_, _) => false;

    public bool IsOpen(StorageId storage) => Open.Contains(storage);
    public ScannedItem? ReadSlot(SlotRef slot) => Slots.GetValueOrDefault(slot);

    public SlotRef? FindSlot(StorageId storage, uint itemId, int quantity, bool isHq, IReadOnlySet<SlotRef> exclude, SlotRef? preferred)
    {
        var hits = Slots.Values.Where(i => StorageId.Of(i.Slot) == storage && i.ItemId == itemId && i.Quantity == quantity && i.IsHq == isHq && !exclude.Contains(i.Slot)).Select(i => i.Slot).ToList();
        if (hits.Count == 0) return null;
        return preferred is { } p && hits.Contains(p) ? p : hits[0];
    }

    public SlotRef? FindLanding(StorageId storage, uint itemId, bool isHq, int quantity, uint preferredPage, IReadOnlySet<SlotRef> reserved, bool emptyOnly = false)
    {
        var page = preferredPage != 0 ? preferredPage : CapacityModel.PagesOf(storage.Kind).First();
        var stackSize = Lookup(itemId)?.StackSize ?? 1;
        var partial = emptyOnly ? null : Slots.Values.FirstOrDefault(i => StorageId.Of(i.Slot) == storage && i.ItemId == itemId && i.IsHq == isHq && !reserved.Contains(i.Slot) && i.Quantity + quantity <= stackSize);
        if (partial is not null) return partial.Slot;
        var size = CapacityModel.DefaultPageSize(page);
        for (var i = 0; i < size; i++)
        {
            var s = new SlotRef(storage.Kind, page, i, storage.OwnerId);
            if (!Slots.ContainsKey(s) && !reserved.Contains(s)) return s;
        }
        return null;
    }

    public IReadOnlyDictionary<(StorageId Storage, uint Page), int> LiveSizes() => new Dictionary<(StorageId, uint), int>();

    public Task<MoveOutcome> MoveAsync(SlotRef from, SlotRef to, uint itemId, int quantity, CancellationToken ct)
    {
        Calls.Add($"{from}->{to}");
        if (Refuse(from, to)) return Task.FromResult(new MoveOutcome(MoveStatus.Refused, "refused by fake"));
        if (!Slots.TryGetValue(from, out var item) || item.ItemId != itemId) return Task.FromResult(new MoveOutcome(MoveStatus.SourceChanged, "gone"));
        Slots.Remove(from);
        if (Slots.TryGetValue(to, out var existing) && existing.ItemId == itemId)
            Slots[to] = existing with { Quantity = existing.Quantity + item.Quantity };
        else
            Slots[to] = item with { Slot = to };
        return Task.FromResult(MoveOutcome.Ok);
    }
}

public class MoveExecutorTests
{
    private static readonly StorageId Bags = new(ContainerKind.Inventory);
    private static readonly StorageId SaddleId = new(ContainerKind.Saddlebag);
    private static readonly StorageId RetA = new(ContainerKind.Retainer, 0xA);
    private static readonly StorageId RetB = new(ContainerKind.Retainer, 0xB);
    private static readonly RunIdentity Who = new(0xC0FFEE, "Test Char");

    private static MoveOp Op(ScannedItem item, StorageId to, MoveLeg leg = MoveLeg.Direct, Guid? id = null, uint page = 0) =>
        new(id ?? Guid.NewGuid(), item, Lookup(item.ItemId)!, StorageId.Of(item.Slot), to, leg, page, 1);

    [Fact]
    public async Task Direct_move_lands_merges_and_is_logged()
    {
        var game = new FakeMoveActions();
        game.Open.Add(SaddleId);
        var widgets = ScannedItem.Simple(Inv(3), 15, 30);
        game.Slots[widgets.Slot] = widgets;
        game.Slots[Saddle(4)] = ScannedItem.Simple(Saddle(4), 15, 60);
        var log = new MemoryMoveLog();

        var report = await new MoveExecutor(game, log, new NoDelay()).ExecuteAsync([Op(widgets, SaddleId)], Who, CancellationToken.None);

        Assert.Equal(1, report.Done);
        Assert.Equal(90, game.Slots[Saddle(4)].Quantity);
        Assert.DoesNotContain(widgets.Slot, game.Slots.Keys);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(ContainerKind.Saddlebag, entry.ToKind);
    }

    [Fact]
    public async Task A_closed_storage_parks_the_move_with_what_to_open()
    {
        var game = new FakeMoveActions();
        var coat = ScannedItem.Simple(Inv(0), 6, 1);
        game.Slots[coat.Slot] = coat;

        var report = await new MoveExecutor(game, new MemoryMoveLog(), new NoDelay()).ExecuteAsync([Op(coat, RetA)], Who, CancellationToken.None);

        Assert.Equal(0, report.Done);
        var pending = Assert.Single(report.Pending);
        Assert.Contains("summon this retainer", report.PendingReasons[pending]);
        Assert.Empty(game.Calls);
    }

    [Fact]
    public async Task A_relay_runs_leg_by_leg_as_storages_open_and_the_second_leg_waits_for_the_first()
    {
        var game = new FakeMoveActions();
        var coat = ScannedItem.Simple(new SlotRef(ContainerKind.Retainer, GameContainerIds.RetainerPage1, 2, 0xA), 6, 1);
        game.Slots[coat.Slot] = coat;
        var id = Guid.NewGuid();
        var legOut = Op(coat, Bags, MoveLeg.RelayOut, id);
        var legIn = new MoveOp(id, coat, Lookup(6)!, Bags, RetB, MoveLeg.RelayIn, 0, 1);
        var exec = new MoveExecutor(game, new MemoryMoveLog(), new NoDelay());

        // Nothing open but the bags: the first leg waits on A, the second leg waits on its first leg.
        var closed = await exec.ExecuteAsync([legOut, legIn], Who, CancellationToken.None);
        Assert.Equal(2, closed.Pending.Count);
        Assert.Contains("summon", closed.PendingReasons[legOut]);

        // Retainer A open: the coat comes into the bags; the second leg still waits for B.
        game.Open.Add(RetA);
        var first = await exec.ExecuteAsync([legOut, legIn], Who, CancellationToken.None);
        Assert.Equal(1, first.Done);
        Assert.Contains(game.Slots.Values, i => i.ItemId == 6 && i.Slot.Kind == ContainerKind.Inventory);
        Assert.Single(first.Pending);

        // Retainer B open: the second leg finds the coat in the bags by identity and puts it away.
        game.Open.Remove(RetA);
        game.Open.Add(RetB);
        var second = await exec.ExecuteAsync([legIn], Who, CancellationToken.None);
        Assert.Equal(1, second.Done);
        Assert.Contains(game.Slots.Values, i => i.ItemId == 6 && i.Slot.OwnerId == 0xB);
    }

    [Fact]
    public async Task Refusals_count_as_failures_and_three_in_a_row_stop_the_run()
    {
        var game = new FakeMoveActions { Refuse = (_, _) => true };
        game.Open.Add(SaddleId);
        var ops = new List<MoveOp>();
        for (var i = 0; i < 5; i++)
        {
            var item = ScannedItem.Simple(Inv(i), 6, 1);
            game.Slots[item.Slot] = item;
            ops.Add(Op(item, SaddleId));
        }

        var report = await new MoveExecutor(game, new MemoryMoveLog(), new NoDelay()).ExecuteAsync(ops, Who, CancellationToken.None);

        Assert.True(report.Aborted);
        Assert.Equal(3, report.Failed);
        // The two it never reached stay where they are and are not queued to move by themselves later.
        Assert.Empty(report.Pending);
        Assert.Equal(2, report.NotReached);
        Assert.Contains("failed in a row", report.AbortReason);
    }

    [Fact]
    public async Task A_relay_never_merges_into_a_stack_the_player_already_has_in_the_bags()
    {
        var game = new FakeMoveActions();
        game.Open.Add(RetA);
        var mine = ScannedItem.Simple(Inv(0), 15, 10);                                              // the player's own partial stack
        var travelling = ScannedItem.Simple(new SlotRef(ContainerKind.Retainer, GameContainerIds.RetainerPage1, 1, 0xA), 15, 5);
        game.Slots[mine.Slot] = mine;
        game.Slots[travelling.Slot] = travelling;
        var id = Guid.NewGuid();
        var exec = new MoveExecutor(game, new MemoryMoveLog(), new NoDelay());

        await exec.ExecuteAsync([Op(travelling, Bags, MoveLeg.RelayOut, id)], Who, CancellationToken.None);

        // Two stacks, not one of fifteen: the second leg can still find exactly the five it has to take.
        Assert.Equal(10, game.Slots[mine.Slot].Quantity);
        Assert.Contains(game.Slots.Values, i => i.ItemId == 15 && i.Quantity == 5 && i.Slot.Kind == ContainerKind.Inventory);

        game.Open.Remove(RetA);
        game.Open.Add(RetB);
        var legIn = new MoveOp(id, travelling, Lookup(15)!, Bags, RetB, MoveLeg.RelayIn, 0, 1);
        var second = await exec.ExecuteAsync([legIn], Who, CancellationToken.None);

        Assert.Equal(1, second.Done);
        Assert.Equal(10, game.Slots[mine.Slot].Quantity);                                          // the player's stack stayed put
        Assert.Contains(game.Slots.Values, i => i.ItemId == 15 && i.Quantity == 5 && i.Slot.OwnerId == 0xB);
    }

    [Fact]
    public async Task A_second_leg_never_ships_the_players_own_look_alike_stack()
    {
        var game = new FakeMoveActions();
        game.Open.Add(RetB);                                                                          // A is closed, B is open
        var coat = ScannedItem.Simple(new SlotRef(ContainerKind.Retainer, GameContainerIds.RetainerPage1, 2, 0xA), 6, 1);
        var theirs = ScannedItem.Simple(Inv(3), 6, 1);                                                // an identical coat already in the bags
        game.Slots[coat.Slot] = coat;
        game.Slots[theirs.Slot] = theirs;
        var id = Guid.NewGuid();
        var legOut = Op(coat, Bags, MoveLeg.RelayOut, id);
        var legIn = new MoveOp(id, coat, Lookup(6)!, Bags, RetB, MoveLeg.RelayIn, 0, 1);

        var report = await new MoveExecutor(game, new MemoryMoveLog(), new NoDelay()).ExecuteAsync([legOut, legIn], Who, CancellationToken.None);

        Assert.Equal(0, report.Done);
        Assert.True(game.Slots.ContainsKey(theirs.Slot));                                            // the player's coat is untouched
        Assert.DoesNotContain(game.Slots.Values, i => i.Slot.OwnerId == 0xB);
    }

    [Fact]
    public async Task No_landing_slot_parks_the_move_instead_of_failing()
    {
        var game = new FakeMoveActions();
        game.Open.Add(SaddleId);
        for (var i = 0; i < 35; i++) game.Slots[Saddle(i)] = ScannedItem.Simple(Saddle(i), 4, 1); // page 1 full of gear
        var coat = ScannedItem.Simple(Inv(0), 6, 1);
        game.Slots[coat.Slot] = coat;

        var report = await new MoveExecutor(game, new MemoryMoveLog(), new NoDelay()).ExecuteAsync([Op(coat, SaddleId)], Who, CancellationToken.None);

        var pending = Assert.Single(report.Pending);
        Assert.Contains("no room", report.PendingReasons[pending]);
        Assert.Equal(0, report.Failed);
    }
}
