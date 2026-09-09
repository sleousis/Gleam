using TidyUp.Core.Execution;
using TidyUp.Core.Logging;
using TidyUp.Core.Model;
using TidyUp.Core.Rules;
using static TidyUp.Core.Tests.EndToEnd.E2e;

namespace TidyUp.Core.Tests.EndToEnd;

/// <summary>The world changes under the run, the game says no, the player hits stop: nothing unplanned may be lost.</summary>
public class ResilienceScenarios
{
    [Fact]
    public async Task Items_that_moved_since_the_review_are_found_by_identity_and_changed_stacks_are_left_alone()
    {
        var world = new FakeWorld { ShopOpen = true };
        world.Add(Bag(0), 1, 14);
        world.Add(Bag(1), 4, 1);
        world.Add(Bag(2), 5, 1);
        var plan = Plan(world, Profile(PresetName.DiscardAll));
        var queue = Queue(plan);
        Assert.Equal(3, queue.Count);

        // Between review and click: the player sorts the bag (the two pieces of gear swap places) and spends a bronze piece.
        var doublet = world.Slots[Bag(1)]; var cap = world.Slots[Bag(2)];
        world.Slots[Bag(1)] = cap with { Slot = Bag(1) };
        world.Slots[Bag(2)] = doublet with { Slot = Bag(2) };
        world.Slots[Bag(0)] = world.Slots[Bag(0)] with { Quantity = 13 };

        var report = await Clean(world, queue);

        Assert.Equal(2, report.Done);
        Assert.Equal(1, report.Skipped);
        Assert.False(world.Has(4)); Assert.False(world.Has(5));
        Assert.Equal(13, world.CountOf(1));                                   // the changed stack survives untouched
        Assert.Contains(report.Results, r => r.Outcome == ActionOutcome.SkippedChanged && r.Action.ItemId == 1);
        Assert.Equal(2, world.Destroyed.Count);
    }

    [Fact]
    public async Task Three_refusals_in_a_row_stop_the_run_and_everything_after_stays_untouched()
    {
        var world = new FakeWorld();
        for (var i = 0; i < 6; i++) world.Add(Bag(i), 1, 10 + i);
        world.FailDiscardWhen = s => s.Slot is 1 or 2 or 3;
        var plan = Plan(world, Profile(PresetName.DiscardAll));

        var report = await Clean(world, Queue(plan));

        Assert.True(report.Aborted);
        Assert.Contains("3 items failed in a row", report.AbortReason);
        Assert.Equal(1, report.Done);
        Assert.Equal(3, report.Failed);
        Assert.Equal(2, report.Pending.Count);
        Assert.All(report.PendingReasons.Values, r => Assert.Contains("earlier failure", r));
        Assert.Equal(5, world.Count(ContainerKind.Inventory));
        Assert.True(world.Has(Bag(4))); Assert.True(world.Has(Bag(5)));
    }

    [Fact]
    public async Task One_refusal_between_successes_is_reported_and_the_run_carries_on()
    {
        var world = new FakeWorld();
        for (var i = 0; i < 5; i++) world.Add(Bag(i), 1, 10 + i);
        world.FailDiscardWhen = s => s.Slot == 2;
        var report = await Clean(world, Queue(Plan(world, Profile(PresetName.DiscardAll))));

        Assert.False(report.Aborted);
        Assert.Equal(4, report.Done);
        Assert.Equal(1, report.Failed);
        Assert.Contains(report.Results, r => r.Outcome == ActionOutcome.Failed && r.Message.Contains("game refused"));
        Assert.Equal(1, world.Count(ContainerKind.Inventory));
    }

    [Fact]
    public async Task Stopping_mid_run_finishes_the_current_item_and_parks_the_rest_for_later()
    {
        var world = new FakeWorld();
        for (var i = 0; i < 5; i++) world.Add(Bag(i), 1, 10 + i);
        using var cts = new CancellationTokenSource();
        var acted = 0;
        world.BeforeAction = () => { if (++acted == 2) cts.Cancel(); };
        var queue = Queue(Plan(world, Profile(PresetName.DiscardAll)));

        var report = await Clean(world, queue, ct: cts.Token);

        Assert.Equal(2, report.Done);                                          // the item in flight when Stop was pressed still finishes
        Assert.Equal(3, report.Pending.Count);
        Assert.All(report.PendingReasons.Values, r => Assert.Contains("stopped", r));
        Assert.Equal(3, world.Count(ContainerKind.Inventory));

        // Later, the parked rows run to completion with no duplicates.
        var later = await Clean(world, Leftover(report));
        Assert.Equal(3, later.Done);
        Assert.Equal(0, world.Count(ContainerKind.Inventory));
    }

    [Fact]
    public async Task Replaying_a_finished_queue_touches_nothing()
    {
        var world = new FakeWorld();
        world.Add(Bag(0), 1, 14);
        world.Add(Bag(1), 4, 1);
        var queue = Queue(Plan(world, Profile(PresetName.DiscardAll)));
        var first = await Clean(world, queue);
        Assert.Equal(2, first.Done);

        world.Add(Bag(0), 1, 14);      // a fresh stack of the same item arrives in the same slot, same size
        var again = await Clean(world, queue);

        // Same slot, same item, same quantity: indistinguishable from the planned row, so it is cleaned once more;
        // the other row has nothing to find and is skipped, never failed.
        Assert.Equal(1, again.Done);
        Assert.Equal(1, again.Skipped);
        Assert.Equal(0, again.Failed);
    }

    [Fact]
    public async Task A_full_bag_holds_materia_gear_back_until_space_frees_up()
    {
        var world = new FakeWorld();
        world.Add(Bag(0), 5, 1, false, 12, 34, 56);                                  // cap with three materia
        for (var i = 1; i < 140; i++) world.Add(Bag(i % 35, (uint)(i / 35)), 11, 1);  // one free slot short of three
        var plan = Plan(world, Profile(PresetName.DiscardAll));
        Row(plan, 5u).Checked = true;                                              // the player accepts the materia warning
        var queue = Queue(plan);
        Assert.Single(queue);

        var blocked = await Clean(world, queue);
        Assert.Single(blocked.Pending);
        Assert.Contains("materia", blocked.PendingReasons.Values.Single());
        Assert.True(world.Has(5));

        world.Slots.Remove(Bag(1)); world.Slots.Remove(Bag(2)); world.Slots.Remove(Bag(3));
        var done = await Clean(world, Leftover(blocked));
        Assert.Equal(1, done.Done);
        Assert.Equal(3, world.CountOf(FakeWorld.MateriaItemId));
        Assert.False(world.Has(5));
    }

    [Fact]
    public async Task Retainer_rows_only_run_while_that_retainer_is_the_one_open()
    {
        var world = new FakeWorld();
        world.Add(Ret(RetA, 0), 1, 9);
        world.Add(Ret(RetB, 0), 1, 8);
        world.OpenRetainer(RetA);
        var plan = Plan(world, Profile(PresetName.DiscardAll));
        Assert.True(plan.Sections.Single(s => s.OwnerId == RetA).IsAvailableNow);
        Assert.False(plan.Sections.Single(s => s.OwnerId == RetB).IsAvailableNow);

        var first = await Clean(world, Queue(plan));
        Assert.Equal(1, first.Done);
        Assert.Single(first.Pending, p => p.Slot.OwnerId == RetB);

        world.OpenRetainer(RetB);
        var second = await Clean(world, Leftover(first));
        Assert.Equal(1, second.Done);
        Assert.Equal(0, world.Count(ContainerKind.Retainer));
    }

    [Fact]
    public async Task Listing_without_a_known_price_waits_instead_of_guessing()
    {
        var world = new FakeWorld();
        world.Add(Bag(0), 6, 1);
        world.OpenRetainer(RetA);
        var plan = Plan(world, Profile(PresetName.MarketBoard));       // no market data at all
        var row = Row(plan, 6u);
        row.ChosenAction = ActionKind.MarketList;                        // the player insists on the market board
        row.Checked = true;

        var report = await Clean(world, Queue(plan));
        Assert.Single(report.Pending);
        Assert.Contains("no market price", report.PendingReasons.Values.Single());
        Assert.Empty(world.Listings);
        Assert.True(world.Has(6));
    }
}
