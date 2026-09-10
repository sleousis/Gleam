using Gleam.Core.Model;
using Gleam.Core.Organizer.Capacity;
using Gleam.Core.Organizer.Model;
using Gleam.Core.Organizer.Solving;
using static Gleam.Core.Tests.TestData;

namespace Gleam.Core.Tests;

public class OrganizerSolverTests
{
    private static readonly StorageId Bags = new(ContainerKind.Inventory);
    private static readonly StorageId SaddleId = new(ContainerKind.Saddlebag);
    private static readonly StorageId RetA = new(ContainerKind.Retainer, 0xA);
    private static readonly StorageId RetB = new(ContainerKind.Retainer, 0xB);
    private static bool NoNeverTouch(uint id, bool hq) => false;

    private static OrganizerPlan Plan(params OrganizerRule[] rules)
    {
        var p = new OrganizerPlan { Name = "test", BagStagingReserve = 2 };
        p.Rules.AddRange(rules);
        return p;
    }

    private static OrganizerRule Rule(string name, OrganizerPredicate when, Destination then) => new() { Name = name, When = when, Then = then };

    private static SolveResult Solve(IReadOnlyList<ScannedItem> items, OrganizerPlan plan, params StorageId[] storages)
    {
        var desired = DesiredStateBuilder.Build(items, plan, Context(), Lookup, NoNeverTouch, new[] { 0xAul, 0xBul });
        var spaces = CapacityModel.Build(items, storages, Lookup);
        return MoveSolver.Solve(desired, spaces, plan);
    }

    [Fact]
    public void Keep_in_bags_holds_back_whole_stacks_up_to_the_number_and_moves_the_rest()
    {
        var rule = Rule("widgets away", new OrganizerPredicate { ItemIds = [15] }, Destination.Saddlebag);
        rule.KeepInBags = 40;
        var plan = Plan(rule);
        var items = new[]
        {
            ScannedItem.Simple(Inv(0), 15, 99),
            ScannedItem.Simple(Inv(1), 15, 30),
            ScannedItem.Simple(Inv(2), 15, 5),
            ScannedItem.Simple(Ret(0, 0xA), 15, 50),   // already elsewhere: untouched by the keep count
        };

        var desired = DesiredStateBuilder.Build(items, plan, Context(), Lookup, NoNeverTouch, new[] { 0xAul });
        var byslot = desired.Placements.ToDictionary(p => p.Item.Slot);
        Assert.False(byslot[Inv(2)].WantsMove);   // 5 kept
        Assert.False(byslot[Inv(1)].WantsMove);   // 5 + 30 = 35 ≤ 40 kept
        Assert.True(byslot[Inv(0)].WantsMove);    // 99 would overshoot: goes
        Assert.Equal(DestinationKind.Saddlebag, byslot[Ret(0, 0xA)].Destination.Kind);
    }

    [Fact]
    public void Keep_in_bags_never_splits_so_a_single_oversized_stack_stays()
    {
        var rule = Rule("widgets away", new OrganizerPredicate { ItemIds = [15] }, Destination.Saddlebag);
        rule.KeepInBags = 10;
        var desired = DesiredStateBuilder.Build([ScannedItem.Simple(Inv(0), 15, 99)], Plan(rule), Context(), Lookup, NoNeverTouch, []);
        Assert.False(desired.Placements.Single().WantsMove);
    }

    [Fact]
    public void First_matching_rule_wins_and_unmatched_items_follow_the_fallback()
    {
        // item 15: stackable widget (Other), item 12: potion (Consumables), item 6: coat (Gear)
        var plan = Plan(
            Rule("potions stay", new OrganizerPredicate { Tags = [ItemTag.Consumables] }, Destination.Bags),
            Rule("everything to saddle", new OrganizerPredicate(), Destination.Saddlebag));
        plan.Fallback = Destination.Stay;
        var items = new[] { ScannedItem.Simple(Inv(0), 12, 3), ScannedItem.Simple(Inv(1), 15, 10) };

        var desired = DesiredStateBuilder.Build(items, plan, Context(), Lookup, NoNeverTouch, []);
        Assert.Equal("potions stay", desired.Placements.Single(p => p.Item.ItemId == 12).Rule!.Name);
        Assert.False(desired.Placements.Single(p => p.Item.ItemId == 12).WantsMove);
        Assert.Equal(DestinationKind.Saddlebag, desired.Placements.Single(p => p.Item.ItemId == 15).Destination.Kind);
        Assert.True(desired.Placements.Single(p => p.Item.ItemId == 15).WantsMove);
    }

    [Fact]
    public void Gearset_pieces_and_non_equipment_bound_for_the_armoury_are_pinned_with_reasons()
    {
        var plan = Plan(
            Rule("gear away", new OrganizerPredicate { Tags = [ItemTag.Gear] }, Destination.AnyRetainer),
            Rule("potions to armoury", new OrganizerPredicate { Tags = [ItemTag.Consumables] }, Destination.Armoury));
        var items = new[] { ScannedItem.Simple(Arm(0), 5, 1), ScannedItem.Simple(Inv(0), 12, 1) };
        var desired = DesiredStateBuilder.Build(items, plan, Context(gearsetItems: [5u]), Lookup, NoNeverTouch, new[] { 0xAul });

        Assert.Empty(desired.Placements);
        Assert.Contains(desired.Pinned, p => p.Item.ItemId == 5 && p.Reason.Contains("gear set"));
        Assert.Contains(desired.Pinned, p => p.Item.ItemId == 12 && p.Reason.Contains("Only equipment"));
    }

    [Fact]
    public void Direct_moves_merge_into_headroom_and_report_the_end_state()
    {
        // 60 widgets already in the saddlebag (headroom 39); 30 more in the bags merge away entirely.
        var plan = Plan(Rule("widgets", new OrganizerPredicate { ItemIds = [15] }, Destination.Saddlebag));
        var items = new[] { ScannedItem.Simple(Saddle(0), 15, 60), ScannedItem.Simple(Inv(0), 15, 30), ScannedItem.Simple(Inv(1), 15, 90) };

        var r = Solve(items, plan, Bags, SaddleId);

        Assert.True(r.Report.Feasible);
        Assert.Equal(2, r.Moves.Count);
        Assert.All(r.Moves, m => Assert.Equal(MoveLeg.Direct, m.Leg));
        Assert.All(r.Moves, m => Assert.Equal(SaddleId, m.To));
        var saddle = r.EndState.Single(e => e.Storage == SaddleId);
        Assert.Equal(1, saddle.UsedBefore);
        Assert.Equal(2, saddle.UsedAfter); // 60+30+9 fill one stack, the remaining 81 form a second
        var bags = r.EndState.Single(e => e.Storage == Bags);
        Assert.Equal(0, bags.UsedAfter);
        Assert.Equal(1, r.Passes);
    }

    [Fact]
    public void A_full_destination_is_reported_as_a_shortfall_with_the_rule_to_blame()
    {
        var plan = Plan(Rule("gear to A", new OrganizerPredicate { Tags = [ItemTag.Gear] }, Destination.RetainerNamed(0xA)));
        var items = new List<ScannedItem>();
        // Retainer A completely full of coats.
        for (var i = 0; i < 175; i++) items.Add(ScannedItem.Simple(new SlotRef(ContainerKind.Retainer, (uint)(GameContainerIds.RetainerPage1 + i / 25), i % 25, 0xA), 6, 1));
        // Three more coats in the bags.
        items.Add(ScannedItem.Simple(Inv(0), 6, 1)); items.Add(ScannedItem.Simple(Inv(1), 6, 1)); items.Add(ScannedItem.Simple(Inv(2), 6, 1));

        var r = Solve(items, plan, Bags, RetA);

        Assert.False(r.Report.Feasible);
        var s = Assert.Single(r.Report.Shortfalls);
        Assert.Equal(RetA, s.Storage);
        Assert.Equal(3, s.Needed);
        Assert.Equal(0, s.Free);
        Assert.Equal(3, s.Short);
        Assert.Equal("gear to A", s.TopRule);
    }

    [Fact]
    public void Retainer_to_retainer_relays_through_the_bags_in_two_legs_and_never_overflows_them()
    {
        var plan = Plan(Rule("coats to B", new OrganizerPredicate { Tags = [ItemTag.Gear] }, Destination.RetainerNamed(0xB)));
        var items = new List<ScannedItem>();
        for (var i = 0; i < 12; i++) items.Add(ScannedItem.Simple(new SlotRef(ContainerKind.Retainer, GameContainerIds.RetainerPage1, i, 0xA), 6, 1));
        // Bags nearly full: 135 of 140 used → 5 free, reserve 2 → 3 coats per wave.
        for (var i = 0; i < 135; i++) items.Add(ScannedItem.Simple(Inv(i % 35, (uint)(i / 35)), 12, 1));

        var r = Solve(items, plan, Bags, RetA, RetB);

        Assert.True(r.Report.Feasible);
        Assert.Equal(12, r.RelayedMoves);
        Assert.Equal(24, r.Moves.Count);
        Assert.All(r.Moves, m => Assert.NotEqual(MoveLeg.Direct, m.Leg));

        // Each RelayIn follows its RelayOut, and bag occupancy never exceeds size - reserve.
        var bagsUsed = 135;
        var outSeen = new HashSet<Guid>();
        foreach (var m in r.Moves)
        {
            if (m.Leg == MoveLeg.RelayOut) { outSeen.Add(m.MoveId); bagsUsed++; }
            else { Assert.Contains(m.MoveId, outSeen); bagsUsed--; }
            Assert.True(bagsUsed <= 140 - plan.BagStagingReserve, $"bags reached {bagsUsed}");
        }
        Assert.Equal(4, r.Passes);                       // 12 items through 3 usable staging slots: four rounds
        Assert.Equal(new[] { RetA, RetB }.ToHashSet(), r.StoragesToOpen.ToHashSet());
    }

    [Fact]
    public void Any_retainer_prefers_one_that_already_holds_the_item_then_the_emptiest()
    {
        var plan = Plan(Rule("widgets to a retainer", new OrganizerPredicate { ItemIds = [15] }, Destination.AnyRetainer));
        var items = new[]
        {
            ScannedItem.Simple(Inv(0), 15, 40),
            ScannedItem.Simple(new SlotRef(ContainerKind.Retainer, GameContainerIds.RetainerPage1, 0, 0xB), 15, 50), // partial stack at B
            ScannedItem.Simple(new SlotRef(ContainerKind.Retainer, GameContainerIds.RetainerPage1, 0, 0xA), 6, 1),
        };

        var r = Solve(items, plan, Bags, RetA, RetB);

        var move = Assert.Single(r.Moves);
        Assert.Equal(RetB, move.To);
        Assert.True(r.Report.Feasible);
    }

    [Fact]
    public void Relays_that_cannot_fit_beside_the_reserve_go_to_a_later_pass()
    {
        var plan = Plan(Rule("coats to B", new OrganizerPredicate { Tags = [ItemTag.Gear] }, Destination.RetainerNamed(0xB)));
        plan.BagStagingReserve = 140; // nothing may pass through
        var items = new List<ScannedItem> { ScannedItem.Simple(new SlotRef(ContainerKind.Retainer, GameContainerIds.RetainerPage1, 0, 0xA), 6, 1) };

        var r = Solve(items, plan, Bags, RetA, RetB);

        Assert.Empty(r.Moves);
        Assert.Contains(r.NoRoom, n => n.Reason.Contains("pass it through"));
    }
}
