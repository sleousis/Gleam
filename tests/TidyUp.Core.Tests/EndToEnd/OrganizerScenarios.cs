using TidyUp.Core.Execution;
using TidyUp.Core.Logging;
using TidyUp.Core.Model;
using TidyUp.Core.Organizer.Capacity;
using TidyUp.Core.Organizer.Model;
using TidyUp.Core.Organizer.Solving;
using TidyUp.Core.Rules;
using static TidyUp.Core.Tests.EndToEnd.E2e;

namespace TidyUp.Core.Tests.EndToEnd;

/// <summary>Whole organizer journeys: preview, open what the moves need, finish, and the storage looks like the layout.</summary>
public class OrganizerScenarios
{
    [Fact]
    public async Task Starter_layout_sorts_a_messy_bag_into_saddlebag_armoury_and_retainer_over_several_visits()
    {
        var world = new FakeWorld();
        world.Add(Bag(0), Materia, 5);
        world.Add(Bag(1), Crystal, 300);          // crystals keep to their own pouch: never moved
        world.Add(Bag(2), 12, 3);                 // potion: consumables stay in the bags
        world.Add(Bag(3), 5, 1);                  // Ironworks Cap: in a gear set → armoury
        world.Add(Bag(4), 4, 1);                  // Doublet: not in a gear set → any retainer
        world.Add(Bag(5), Table, 1);              // housing → any retainer
        var layout = OrganizerPlan.Starter();
        var ctx = Context(gearset: [5u]);

        var preview = Preview(world, layout, ctx);
        Assert.True(preview.Report.Feasible);
        Assert.Equal(4, preview.Moves.Count);
        Assert.DoesNotContain(preview.Moves, m => m.Item.ItemId == 12);
        Assert.DoesNotContain(preview.Moves, m => m.Item.ItemId == Crystal);
        Assert.Equal(2, preview.StoragesToOpen.Count());     // the saddlebag and one retainer

        // Nothing is open: only the armoury move can happen now.
        var first = await Organize(world, preview.Moves);
        Assert.Equal(1, first.Done);
        Assert.Equal(3, first.Pending.Count);
        Assert.Equal(1, world.Count(ContainerKind.Armoury));
        Assert.True(world.Has(Arm(0)));

        var rounds = await OrganizeEverywhere(world, first.Pending);
        Assert.True(rounds <= 2, $"one saddlebag visit and one retainer visit should do; took {rounds}");
        Assert.Equal(5, world.Slots.Values.Where(i => i.Slot.Kind == ContainerKind.Saddlebag && i.ItemId == Materia).Sum(i => i.Quantity));
        Assert.Equal(1, world.Count(ContainerKind.Saddlebag));                  // the materia
        Assert.Equal(2, world.Count(ContainerKind.Retainer, RetA));            // both "any retainer" items on the same retainer
        Assert.Equal(2, world.Count(ContainerKind.Inventory));           // the potion and the crystals are left
        Assert.True(world.Has(Bag(1)));
        Assert.True(world.Has(Bag(2)));
    }

    [Fact]
    public void Preview_refuses_when_a_destination_would_overflow_and_names_the_rule()
    {
        var world = new FakeWorld();
        world.PageSizes[(Saddle, GameContainerIds.SaddleBag1)] = 2;
        world.PageSizes[(Saddle, GameContainerIds.SaddleBag2)] = 0;
        for (var i = 0; i < 5; i++) world.Add(Bag(i), 4, 1);          // five doublets, two slots
        var layout = Layout(Rule("gear to the saddlebag", new OrganizerPredicate { Tags = [ItemTag.Gear] }, Destination.Saddlebag));

        var preview = Preview(world, layout);

        Assert.False(preview.Report.Feasible);
        var shortfall = Assert.Single(preview.Report.Shortfalls);
        Assert.Equal(Saddle, shortfall.Storage);
        Assert.Equal(3, shortfall.Short);
        Assert.Equal("gear to the saddlebag", shortfall.TopRule);
        Assert.Contains(preview.EndState, e => e.Storage == Saddle && e.SizesAreLive);
    }

    [Fact]
    public async Task Retainer_to_retainer_moves_relay_through_the_bags_and_the_bags_never_overflow()
    {
        var world = new FakeWorld();
        for (var i = 0; i < 25; i++) world.Add(Ret(RetA, i), 4, 1);                    // Alpha's whole first page is doublets
        for (var i = 0; i < 125; i++) world.Add(Bag(i % 35, (uint)(i / 35)), 11, 1);      // bags: 15 free slots
        var layout = Layout(Rule("gear to Bravo", new OrganizerPredicate { Tags = [ItemTag.Gear] }, Destination.RetainerNamed(RetB)));
        layout.BagStagingReserve = 5;

        var preview = Preview(world, layout);
        Assert.True(preview.Report.Feasible);
        Assert.Equal(25, preview.Moves.Count(m => m.Leg == MoveLeg.RelayOut));
        Assert.Equal(25, preview.Moves.Count(m => m.Leg == MoveLeg.RelayIn));
        Assert.Equal(3, preview.Passes);                                                 // 10 usable staging slots for 25 items
        Assert.Equal(10, preview.Moves.Count(m => m.Pass == 1 && m.Leg == MoveLeg.RelayOut));

        var log = new MemoryMoveLog();
        var rounds = await OrganizeEverywhere(world, preview.Moves, log);

        Assert.Equal(0, world.Count(ContainerKind.Retainer, RetA));
        Assert.Equal(25, world.Count(ContainerKind.Retainer, RetB));
        Assert.Equal(125, world.Count(ContainerKind.Inventory));                          // the bags are back to how they were
        Assert.True(world.MaxBagOccupancy <= world.BagCapacity - layout.BagStagingReserve, $"bags peaked at {world.MaxBagOccupancy}");
        Assert.Equal(50, log.Entries.Count);
        Assert.Equal(6, rounds);                                                          // three waves, each an Alpha visit and a Bravo visit
    }

    [Fact]
    public async Task Keep_N_in_the_bags_leaves_the_smallest_stacks_and_moves_the_rest()
    {
        var world = new FakeWorld { SaddlebagOpen = true };
        world.Add(Bag(0), 15, 99);
        world.Add(Bag(1), 15, 30);
        world.Add(Bag(2), 15, 5);
        var layout = Layout(Rule("widgets away", new OrganizerPredicate { ItemIds = [15] }, Destination.Saddlebag, keepInBags: 40));

        var preview = Preview(world, layout);
        var move = Assert.Single(preview.Moves);
        Assert.Equal(99, move.Item.Quantity);

        var report = await Organize(world, preview.Moves);
        Assert.Equal(1, report.Done);
        Assert.Equal(35, world.Slots.Values.Where(i => i.Slot.Kind == ContainerKind.Inventory).Sum(i => i.Quantity));
        Assert.Equal(99, world.Slots.Values.Where(i => i.Slot.Kind == ContainerKind.Saddlebag).Sum(i => i.Quantity));
    }

    [Fact]
    public async Task Incoming_stacks_top_up_partial_stacks_only_when_the_layout_says_so()
    {
        var world = new FakeWorld { SaddlebagOpen = true };
        world.Add(Bag(0), 15, 30);
        world.Add(Saddlebag(4), 15, 60);
        var layout = Layout(Rule("widgets", new OrganizerPredicate { ItemIds = [15] }, Destination.Saddlebag));

        var merging = await Organize(world, Preview(world, layout).Moves);
        Assert.Equal(1, merging.Done);
        Assert.Equal(1, world.Count(ContainerKind.Saddlebag));
        Assert.Equal(90, world.Slots[Saddlebag(4)].Quantity);

        var world2 = new FakeWorld { SaddlebagOpen = true };
        world2.Add(Bag(0), 15, 30);
        world2.Add(Saddlebag(4), 15, 60);
        layout.MergeStacksAtDestination = false;
        var preview = Preview(world2, layout);
        Assert.Single(preview.Moves);
        Assert.Contains(preview.EndState, e => e.Storage == Saddle && e.UsedAfter == 2);
    }

    [Fact]
    public async Task Moves_whose_source_changed_are_skipped_and_a_refusal_does_not_stop_the_rest()
    {
        var world = new FakeWorld { SaddlebagOpen = true };
        world.Add(Bag(0), Materia, 3);
        world.Add(Bag(1), Materia, 4);
        world.Add(Bag(2), Materia, 5);
        world.Add(Bag(3), Materia, 6);
        var layout = Layout(Rule("materia", new OrganizerPredicate { Tags = [ItemTag.Materia] }, Destination.Saddlebag));
        layout.MergeStacksAtDestination = false;
        var preview = Preview(world, layout);
        Assert.Equal(4, preview.Moves.Count);

        world.Slots.Remove(Bag(1));                                        // used up before the run
        world.RefuseMove = (from, _) => from == Bag(2);                    // the game says no once

        var report = await Organize(world, preview.Moves);
        Assert.False(report.Aborted);
        Assert.Equal(2, report.Done);
        Assert.Equal(1, report.Skipped);
        Assert.Equal(1, report.Failed);
        Assert.True(world.Has(Bag(2)));                                    // refused stack is exactly where it was
        Assert.Equal(2, world.Count(ContainerKind.Saddlebag));
    }

    [Fact]
    public async Task A_saddlebag_that_fills_up_along_the_way_parks_the_rest_without_hammering_the_game()
    {
        var world = new FakeWorld { SaddlebagOpen = true };
        world.PageSizes[(Saddle, GameContainerIds.SaddleBag1)] = 3;
        world.PageSizes[(Saddle, GameContainerIds.SaddleBag2)] = 0;
        world.Add(Bag(0), 15, 99);
        world.Add(Bag(1), 15, 99);
        world.Add(Bag(2), 15, 99);
        var layout = Layout(Rule("widgets", new OrganizerPredicate { ItemIds = [15] }, Destination.Saddlebag));
        var preview = Preview(world, layout);
        Assert.True(preview.Report.Feasible);
        Assert.Equal(3, preview.Moves.Count);

        // Between the preview and the run something else lands in the saddlebag.
        world.Add(Saddlebag(1), 12, 1);
        world.Add(Saddlebag(2), 12, 1);

        var report = await Organize(world, preview.Moves);
        Assert.Equal(1, report.Done);
        Assert.Equal(2, report.Pending.Count);
        Assert.All(report.PendingReasons.Values, r => Assert.Contains("no room", r));
        Assert.Equal(1, world.Calls.Count(c => c.StartsWith("move:")));         // the two that cannot land never reach the game
        Assert.False(report.Aborted);
    }

    [Fact]
    public async Task A_stack_only_merges_into_one_that_can_take_all_of_it()
    {
        var world = new FakeWorld { SaddlebagOpen = true };
        world.Add(Bag(0), 15, 30);
        world.Add(Saddlebag(0), 15, 90);          // only 9 room: not a merge target for 30
        world.PageSizes[(Saddle, GameContainerIds.SaddleBag1)] = 1;
        world.PageSizes[(Saddle, GameContainerIds.SaddleBag2)] = 0;
        var layout = Layout(Rule("widgets", new OrganizerPredicate { ItemIds = [15] }, Destination.Saddlebag));

        var preview = Preview(world, layout);
        Assert.False(preview.Report.Feasible);                                  // the model knows a merge cannot take it
        Assert.Equal(1, Assert.Single(preview.Report.Shortfalls).Short);

        var report = await Organize(world, preview.Moves);                       // and the executor agrees
        Assert.Equal(0, report.Done);
        Assert.DoesNotContain(world.Calls, c => c.StartsWith("move:"));
        Assert.Equal(90, world.Slots[Saddlebag(0)].Quantity);
    }

    [Fact]
    public async Task Gear_set_pieces_are_pinned_away_from_retainers_and_the_saddlebag()
    {
        var world = new FakeWorld { SaddlebagOpen = true };
        world.Add(Bag(0), 5, 1);          // in a gear set
        world.Add(Bag(1), 4, 1);          // not
        var layout = Layout(Rule("all gear away", new OrganizerPredicate { Tags = [ItemTag.Gear] }, Destination.Saddlebag));

        var preview = Preview(world, layout, Context(gearset: [5u]));
        Assert.Single(preview.Moves);
        Assert.Contains(preview.Pinned, p => p.Item.ItemId == 5 && p.Reason.Contains("gear set"));

        await OrganizeEverywhere(world, preview.Moves);
        Assert.True(world.Has(Bag(0)));
        Assert.Equal(1, world.Count(ContainerKind.Saddlebag));
    }

    [Fact]
    public async Task Cleaning_first_then_organizing_leaves_the_layout_the_rules_describe()
    {
        var world = new FakeWorld { ShopOpen = true };
        world.Add(Bag(0), 1, 14);                 // junk → sold
        world.Add(Bag(1), Materia, 12);           // → saddlebag
        world.Add(Bag(2), 4, 1);                  // obsolete gear → sold by the cleaner before the organizer sees it
        world.Add(Bag(3), Table, 1);              // → retainer
        world.Add(Bag(4), 5, 1);                  // gear set piece → armoury

        var cleaned = await Clean(world, Queue(Plan(world, ctx: Context(gearset: [5u]))));
        Assert.True(cleaned.Done >= 2);
        Assert.False(world.Has(1)); Assert.False(world.Has(4));
        Assert.True(world.Has(5));                                          // gear set pieces are never cleaned

        world.ShopOpen = false;
        var preview = Preview(world, OrganizerPlan.Starter(), Context(gearset: [5u]));
        Assert.True(preview.Report.Feasible);
        await OrganizeEverywhere(world, preview.Moves);

        Assert.Equal(0, world.Count(ContainerKind.Inventory));
        Assert.Equal(12, world.Slots.Values.Where(i => i.Slot.Kind == ContainerKind.Saddlebag).Sum(i => i.Quantity));
        Assert.True(world.Has(Arm(0)));
        Assert.Equal(1, world.Count(ContainerKind.Retainer));
    }

    [Fact]
    public void Layouts_survive_a_round_trip_through_sharing_and_still_solve_the_same_way()
    {
        var world = new FakeWorld();
        world.Add(Bag(0), Materia, 5);
        world.Add(Bag(1), 4, 1);
        var original = OrganizerPlan.Starter();
        original.Rules[0].KeepInBags = 2;

        var shared = OrganizerPlanCodec.TryImport(OrganizerPlanCodec.Export(original), new[] { RetA, RetB })!;
        var a = Preview(world, original);
        var b = Preview(world, shared);

        Assert.Equal(a.Moves.Count, b.Moves.Count);
        Assert.Equal(a.Moves.Select(m => (m.Item.Slot, m.To.Kind)), b.Moves.Select(m => (m.Item.Slot, m.To.Kind)));
    }
}
