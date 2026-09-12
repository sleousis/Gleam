using Gleam.Core.Execution;
using Gleam.Core.Model;
using Gleam.Core.Organizer.Capacity;
using Gleam.Core.Organizer.Execution;
using Gleam.Core.Organizer.Solving;
using Gleam.Core.Planning;
using Gleam.Core.Rules;
using static Gleam.Core.Tests.TestData;

namespace Gleam.Core.Tests;

/// <summary>
/// What the list remembers between one look and the next: the player's ticks across a re-scan, rows still
/// waiting for a closed container when the button is pressed again, and organizer moves left waiting.
/// </summary>
public class RescanAndPendingTests
{
    private static PlanRow Row(SlotRef slot, uint itemId = 1, int qty = 1, bool ticked = true, ActionKind action = ActionKind.Discard,
        bool hq = false) => new()
    {
        Proposal = new Proposal
        {
            Item = ScannedItem.Simple(slot, itemId, qty, hq), Info = Items[itemId], Action = action, Confidence = Confidence.High,
            RuleId = VendorOnlyJunkRule.RuleId, Reason = "test",
        },
        Checked = ticked,
        ChosenAction = action,
    };

    private static RunPlan Plan(params PlanRow[] rows)
    {
        var plan = new RunPlan { CharacterId = 1, CharacterName = "Someone" };
        var section = new PlanSection { Kind = ContainerKind.Inventory, OwnerId = 0, OwnerName = string.Empty };
        section.Rows.AddRange(rows);
        plan.Sections.Add(section);
        return plan;
    }

    private static QueuedAction Q(SlotRef slot, uint itemId = 1, int qty = 1, bool hq = false) =>
        new(slot, itemId, qty, hq, ActionKind.Discard, false, "item", 0, VendorOnlyJunkRule.RuleId);

    // ---------- ticks across a re-scan ----------

    [Fact]
    public void An_untick_survives_a_rescan_that_finds_the_item_in_another_slot()
    {
        var before = Plan(Row(Ret(3), qty: 5, ticked: false));
        // The planner ticks it again by default; the retainer's live slot differs from the cached one.
        var fresh = Plan(Row(Ret(9), qty: 5, ticked: true));

        RescanTicks.CarryOver(before, fresh, userAsked: false);

        Assert.False(fresh.AllRows.Single().Checked);
    }

    [Fact]
    public void A_tick_the_player_made_survives_a_rescan()
    {
        var before = Plan(Row(Inv(0), ticked: true));
        var fresh = Plan(Row(Inv(0), ticked: false));

        RescanTicks.CarryOver(before, fresh, userAsked: true);

        Assert.True(fresh.AllRows.Single().Checked);
    }

    [Fact]
    public void A_carried_tick_never_lands_on_a_row_that_now_keeps_the_item()
    {
        var before = Plan(Row(Inv(0), ticked: true));
        var fresh = Plan(Row(Inv(0), ticked: false, action: ActionKind.None));

        RescanTicks.CarryOver(before, fresh, userAsked: false);

        Assert.False(fresh.AllRows.Single().Checked);
    }

    [Fact]
    public void Loot_that_lands_while_the_window_is_open_starts_unticked()
    {
        var before = Plan(Row(Inv(0), ticked: true));
        var loot = Row(Inv(1), itemId: 7, ticked: true);
        var fresh = Plan(Row(Inv(0), ticked: true), loot);

        RescanTicks.CarryOver(before, fresh, userAsked: false);

        Assert.False(loot.Checked);
    }

    [Fact]
    public void A_new_row_keeps_the_planners_tick_when_the_player_asked_for_the_scan()
    {
        var before = Plan(Row(Inv(0), ticked: true));
        var found = Row(Inv(1), itemId: 7, ticked: true);
        var fresh = Plan(Row(Inv(0), ticked: true), found);

        RescanTicks.CarryOver(before, fresh, userAsked: true);

        Assert.True(found.Checked);
    }

    [Fact]
    public void A_stack_that_grew_is_a_new_row()
    {
        var before = Plan(Row(Inv(0), qty: 5, ticked: true));
        var grown = Row(Inv(0), qty: 6, ticked: true);

        RescanTicks.CarryOver(before, Plan(grown), userAsked: false);

        Assert.False(grown.Checked);
    }

    [Fact]
    public void The_same_item_with_two_retainers_keeps_two_ticks()
    {
        var before = Plan(Row(Ret(0, 0xA), ticked: true), Row(Ret(0, 0xB), ticked: false));
        var a = Row(Ret(4, 0xA), ticked: false);
        var b = Row(Ret(4, 0xB), ticked: true);

        RescanTicks.CarryOver(before, Plan(a, b), userAsked: false);

        Assert.True(a.Checked);
        Assert.False(b.Checked);
    }

    [Fact]
    public void Without_an_earlier_list_the_planners_ticks_stand()
    {
        var row = Row(Inv(0), ticked: true);

        RescanTicks.CarryOver(null, Plan(row), userAsked: false);

        Assert.True(row.Checked);
    }

    [Fact(Skip = "Known issue, reported, not fixed here: two identical stacks share one identity, so the first one's tick "
                 + "is carried to both and a copy the player unticked comes back ticked after any re-scan.")]
    public void Unticking_one_of_two_identical_stacks_survives_a_rescan()
    {
        var before = Plan(Row(Inv(0), itemId: 4, ticked: true), Row(Inv(1), itemId: 4, ticked: false));
        var first = Row(Inv(0), itemId: 4, ticked: true);
        var second = Row(Inv(1), itemId: 4, ticked: false);

        RescanTicks.CarryOver(before, Plan(first, second), userAsked: false);

        Assert.False(second.Checked);
    }

    // ---------- rows still waiting when the button is pressed again ----------

    [Fact]
    public void Rows_waiting_for_a_closed_container_ride_along_first()
    {
        var waiting = Q(Saddle(0), itemId: 7);
        var queue = new List<QueuedAction> { Q(Inv(0)) };

        var merged = PendingMerge.Merge([waiting], queue, [Row(Inv(0), ticked: true)]);

        Assert.Equal([waiting, queue[0]], merged);
    }

    [Fact]
    public void A_waiting_row_for_an_item_the_player_has_since_unticked_is_dropped_wherever_it_sits()
    {
        var waiting = Q(Ret(2), itemId: 7, qty: 3);
        // The list now shows the same item in another slot, and a different stack size, unticked.
        var rows = new[] { Row(Ret(8), itemId: 7, qty: 4, ticked: false), Row(Inv(0), ticked: true) };

        var merged = PendingMerge.Merge([waiting], [Q(Inv(0))], rows);

        Assert.DoesNotContain(waiting, merged);
    }

    [Fact]
    public void An_untick_for_another_item_or_another_retainer_does_not_drop_a_waiting_row()
    {
        var waiting = Q(Ret(2, 0xA), itemId: 7);
        var rows = new[] { Row(Ret(2, 0xB), itemId: 7, ticked: false), Row(Ret(3, 0xA), itemId: 1, ticked: false), Row(Ret(4, 0xA), itemId: 7, hq: true, ticked: false) };

        var merged = PendingMerge.Merge([waiting], [Q(Inv(0))], rows);

        Assert.Contains(waiting, merged);
    }

    [Fact]
    public void A_waiting_row_gives_way_to_a_new_row_for_the_same_slot()
    {
        var waiting = Q(Inv(0), itemId: 7);
        var fresh = Q(Inv(0), itemId: 1);

        var merged = PendingMerge.Merge([waiting], [fresh], [Row(Inv(0), ticked: true)]);

        Assert.Equal([fresh], merged);
    }

    // ---------- organizer moves left waiting ----------

    private static readonly StorageId Bags = new(ContainerKind.Inventory);
    private static readonly StorageId Saddlebag = new(ContainerKind.Saddlebag);
    private static readonly StorageId RetainerA = new(ContainerKind.Retainer, 0xA);

    private static MoveOp Move(SlotRef from, StorageId to, MoveLeg leg = MoveLeg.Direct, int qty = 1) =>
        new(Guid.NewGuid(), ScannedItem.Simple(from, 6, qty), Lookup(6)!, new StorageId(from.Kind, from.OwnerId), to, leg, 0, 1);

    [Fact]
    public void The_same_move_from_another_solve_is_the_same_move_whatever_its_id()
    {
        var a = Move(Inv(0), Saddlebag);

        Assert.True(PendingMoveGate.SameMove(a, a with { MoveId = Guid.NewGuid(), PreferredPage = 3, Pass = 2 }));
        Assert.False(PendingMoveGate.SameMove(a, a with { To = RetainerA }));
        Assert.False(PendingMoveGate.SameMove(a, a with { Leg = MoveLeg.RelayOut }));
        Assert.False(PendingMoveGate.SameMove(a, Move(Inv(1), Saddlebag)));
        Assert.False(PendingMoveGate.SameMove(a, Move(Inv(0), Saddlebag, qty: 2)));
    }

    [Fact]
    public void Waiting_moves_the_rewritten_layout_no_longer_wants_are_dropped_and_their_relays_forgotten()
    {
        var stillWanted = Move(Inv(0), Saddlebag);
        var noLongerWanted = Move(Ret(0, 0xA), Bags, MoveLeg.RelayOut);
        var pending = new List<MoveOp> { stillWanted, noLongerWanted };
        var relays = new RelayLedger();
        relays.Record(stillWanted.MoveId, Inv(0));
        relays.Record(noLongerWanted.MoveId, Inv(5));
        // The fresh solve hands out new ids for the move it still wants.
        var current = new List<MoveOp> { stillWanted with { MoveId = Guid.NewGuid() }, Move(Inv(2), RetainerA) };

        var dropped = PendingMoveGate.DropStale(pending, current, relays);

        Assert.Equal([noLongerWanted], dropped);
        Assert.Equal([stillWanted], pending);
        Assert.True(relays.TryGet(stillWanted.MoveId, out _));
        Assert.False(relays.TryGet(noLongerWanted.MoveId, out _));
    }

    [Fact]
    public void A_solve_with_no_moves_drops_everything_waiting()
    {
        var pending = new List<MoveOp> { Move(Inv(0), Saddlebag), Move(Inv(1), RetainerA) };

        PendingMoveGate.DropStale(pending, [], new RelayLedger());

        Assert.Empty(pending);
    }

    [Fact]
    public void A_move_left_waiting_is_never_added_twice()
    {
        var waiting = Move(Inv(0), Saddlebag);
        var pending = new List<MoveOp> { waiting };

        PendingMoveGate.AddWaiting(pending, [waiting with { MoveId = Guid.NewGuid() }, Move(Inv(1), Saddlebag), Move(Inv(1), Saddlebag)]);

        Assert.Equal(2, pending.Count);
        Assert.Same(waiting, pending[0]);
    }
}
