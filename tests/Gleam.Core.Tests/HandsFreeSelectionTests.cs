using Gleam.Core.Execution;
using Gleam.Core.Lists;
using Gleam.Core.Model;
using Gleam.Core.Planning;
using Gleam.Core.Rules;
using Gleam.Core.Settings;
using static Gleam.Core.Tests.TestData;

namespace Gleam.Core.Tests;

/// <summary>
/// What a hands-free run and the after-ventures clean may touch while the player is not looking at the list,
/// and which stop of the trip each ticked row goes to.
/// </summary>
public class HandsFreeSelectionTests
{
    private static readonly IReadOnlySet<string> NoSkips = new HashSet<string>();
    private static readonly IReadOnlySet<(ContainerKind, ulong, uint, bool)> NothingReviewed = new HashSet<(ContainerKind, ulong, uint, bool)>();

    private static PlanRow Row(SlotRef slot, uint itemId = 1, ActionKind action = ActionKind.Discard, bool ticked = true,
        Confidence confidence = Confidence.High, string rule = VendorOnlyJunkRule.RuleId, bool hq = false, string[]? warnings = null) => new()
    {
        Proposal = new Proposal
        {
            Item = ScannedItem.Simple(slot, itemId, 1, hq), Info = Items[itemId], Action = action, Confidence = confidence,
            RuleId = rule, Reason = "test", Warnings = warnings ?? [],
        },
        Checked = ticked,
        ChosenAction = action,
    };

    private static QueuedAction Q(SlotRef slot, ActionKind action, bool broughtHome = false) =>
        new(slot, 1, 1, false, action, false, "Allagan Bronze Piece", 0, "r", BroughtHome: broughtHome);

    private static PlannerInputs Inputs(ItemList protect, ItemList always, Func<uint, ItemInfo?>? lookup = null) => new()
    {
        IncludeUnproposed = true,
        Context = Context(),
        Profile = MakeProfile(),
        InfoLookup = lookup ?? Lookup,
        ProtectList = protect,
        AlwaysDiscardList = always,
        SessionSkips = new HashSet<string>(),
        IsAvailable = (_, _) => true,
        RetainerNames = new Dictionary<ulong, string> { [0xBEEF] = "Retainer B" },
    };

    // ---------- turning rows into a queue ----------

    [Fact]
    public void A_row_the_player_unticked_is_never_queued()
    {
        var queue = PlanQueue.Build([Row(Inv(0), ticked: true), Row(Inv(1), ticked: false)], _ => true);

        Assert.Equal([Inv(0)], queue.Select(q => q.Slot));
    }

    [Fact]
    public void A_keep_row_is_never_queued_ticked_or_not()
    {
        var keep = Row(Inv(0), action: ActionKind.None, ticked: true);

        Assert.Empty(PlanQueue.Build([keep], _ => true));
        Assert.Empty(PlanQueue.Build([keep], _ => true, requireChecked: false));
    }

    [Fact]
    public void The_queue_carries_the_action_the_player_picked_for_the_row()
    {
        var row = Row(Inv(0), action: ActionKind.Discard);
        row.ChosenAction = ActionKind.VendorSell;

        Assert.Equal(ActionKind.VendorSell, Assert.Single(PlanQueue.Build([row], _ => true)).Action);
    }

    // ---------- rows a hands-free run finds once a container opens ----------

    [Fact]
    public void Nothing_found_later_is_cleaned_when_the_player_chose_to_leave_it_for_the_review()
    {
        PlanRow[] rows = [Row(Ret(0))];

        Assert.False(UnattendedSelection.CleansUnseen(UnseenRowsMode.Skip));
        Assert.Empty(UnattendedSelection.PickUnseen(rows, UnseenRowsMode.Skip, NoSkips, NothingReviewed));
        Assert.Single(UnattendedSelection.PickUnseen(rows, UnseenRowsMode.Clean, NoSkips, NothingReviewed));
    }

    [Fact]
    public void An_item_the_player_unticked_in_the_review_is_not_picked_again_after_its_slot_changed()
    {
        // The review showed the retainer's cached slots; the open retainer reports different ones.
        var reviewed = UnattendedSelection.Reviewed([Row(Ret(3), ticked: false)]);

        Assert.Empty(UnattendedSelection.PickUnseen([Row(Ret(11))], UnseenRowsMode.Clean, NoSkips, reviewed));
    }

    [Fact]
    public void The_same_item_with_another_retainer_or_in_hq_counts_as_unseen()
    {
        var reviewed = UnattendedSelection.Reviewed([Row(Ret(0, retainer: 0xA))]);

        var picked = UnattendedSelection.PickUnseen([Row(Ret(0, retainer: 0xB)), Row(Ret(1, retainer: 0xA), hq: true)], UnseenRowsMode.Clean, NoSkips, reviewed);

        Assert.Equal(2, picked.Count);
    }

    [Fact]
    public void A_row_skipped_this_session_is_not_picked()
    {
        var row = Row(Ret(0));

        Assert.Empty(UnattendedSelection.PickUnseen([row], UnseenRowsMode.Clean, new HashSet<string> { row.Key }, NothingReviewed));
    }

    [Fact]
    public void Only_what_a_rule_would_tick_by_itself_is_picked_whatever_the_tick_says()
    {
        var ruleWouldTick = Row(Ret(0), ticked: false);
        var unsure = Row(Ret(1), confidence: Confidence.Medium, ticked: true);
        var warned = Row(Ret(2), warnings: ["Seasonal item. Check before cleaning"], ticked: true);
        var handPicked = Row(Ret(3), rule: RunPlanner.HandPickRuleId, ticked: true);

        var picked = UnattendedSelection.PickUnseen([ruleWouldTick, unsure, warned, handPicked], UnseenRowsMode.Clean, NoSkips, NothingReviewed);

        Assert.Equal([Ret(0)], picked.Select(q => q.Slot));
    }

    [Fact]
    public void Protected_and_hard_blocked_items_are_never_picked_from_a_real_plan()
    {
        var protect = new ItemList(); protect.Add(1);
        var always = new ItemList(); always.Add(15); always.Add(10);
        ScannedItem[] items = [ScannedItem.Simple(Ret(0), 1, 14), ScannedItem.Simple(Ret(1), 15, 3), ScannedItem.Simple(Ret(2), 10, 1)];
        var plan = new RunPlanner().Build(items, Inputs(protect, always));

        var picked = UnattendedSelection.PickUnseen(plan.AllRows, UnseenRowsMode.Clean, NoSkips, NothingReviewed);

        Assert.Equal([15u], picked.Select(q => q.ItemId));
    }

    // ---------- after a retainer's ventures ----------

    [Fact]
    public void After_ventures_only_discards_in_the_bags_are_picked()
    {
        PlanRow[] rows =
        [
            Row(Inv(0), action: ActionKind.Discard),
            Row(Inv(1), action: ActionKind.VendorSell),
            Row(Inv(2), action: ActionKind.MarketList),
            Row(Inv(3), action: ActionKind.Desynth),
            Row(Arm(0), itemId: 4, action: ActionKind.Discard),
            Row(Saddle(0), action: ActionKind.Discard),
            Row(Ret(0), action: ActionKind.Discard),
        ];

        Assert.Equal([Inv(0)], UnattendedSelection.PickAfterVentures(rows, NoSkips).Select(q => q.Slot));
    }

    [Fact]
    public void After_ventures_skipped_hand_picked_and_unsure_rows_are_left()
    {
        var skipped = Row(Inv(0));
        PlanRow[] rows =
        [
            skipped,
            Row(Inv(1), rule: RunPlanner.HandPickRuleId),
            Row(Inv(2), confidence: Confidence.Low),
            Row(Inv(3), warnings: ["Seasonal item. Check before cleaning"]),
        ];

        Assert.Empty(UnattendedSelection.PickAfterVentures(rows, new HashSet<string> { skipped.Key }));
    }

    [Fact]
    public void After_ventures_a_row_the_player_set_to_keep_or_sell_is_left()
    {
        var kept = Row(Inv(0));
        kept.ChosenAction = ActionKind.None;
        var sold = Row(Inv(1));
        sold.ChosenAction = ActionKind.VendorSell;

        Assert.Empty(UnattendedSelection.PickAfterVentures([kept, sold], NoSkips));
    }

    [Fact]
    public void After_ventures_protected_items_are_never_picked_from_a_real_plan()
    {
        // Tradeable with no vendor price: on the always-clean list it becomes a plain discard the rules tick.
        var leftover = ItemInfo.Test(900, "Venture Leftover", vendor: 0, marketable: false);
        var protect = new ItemList(); protect.Add(1);
        // On both lists, the protect list wins.
        var always = new ItemList(); always.Add(900); always.Add(1);
        ScannedItem[] items = [ScannedItem.Simple(Inv(0), 1, 14), ScannedItem.Simple(Inv(1), 900, 1)];
        var plan = new RunPlanner().Build(items, Inputs(protect, always, id => id == 900 ? leftover : Lookup(id)));

        var picked = UnattendedSelection.PickAfterVentures(plan.AllRows, NoSkips);

        Assert.Equal([900u], picked.Select(q => q.ItemId));
    }

    [Fact(Skip = "Known issue, reported, not fixed here: an untick is remembered by slot, so once the sort after a clean "
                 + "moves the stack, the after-ventures clean picks it again. Unseen rows are guarded by item; this path is not.")]
    public void After_ventures_an_item_the_player_unticked_stays_left_after_the_bags_are_sorted()
    {
        var unticked = Row(Inv(0), ticked: false);
        var skips = new HashSet<string> { unticked.Key };
        // The same stack one slot along, as the next scan sees it. The re-scan carried the untick over to it.
        var afterSort = Row(Inv(1), ticked: false);

        Assert.Empty(UnattendedSelection.PickAfterVentures([afterSort], skips));
    }

    // ---------- the stops of a trip ----------

    [Fact]
    public void Bag_and_armoury_rows_without_an_npc_are_done_where_the_player_stands()
    {
        var trip = TripPartition.Of([Q(Inv(0), ActionKind.Discard), Q(Inv(1), ActionKind.Desynth), Q(Arm(0), ActionKind.Discard)]);

        Assert.Equal([Inv(0), Inv(1), Arm(0)], trip.Here.Select(q => q.Slot));
        Assert.Empty(trip.Sells.Concat(trip.Seals).Concat(trip.Listings));
    }

    [Fact]
    public void Sells_seals_and_listings_from_the_bags_wait_for_their_npc()
    {
        var trip = TripPartition.Of([Q(Inv(0), ActionKind.VendorSell), Q(Arm(0), ActionKind.ExpertDelivery), Q(Inv(1), ActionKind.MarketList)]);

        Assert.Empty(trip.Here);
        Assert.Equal([Inv(0)], trip.Sells.Select(q => q.Slot));
        Assert.Equal([Arm(0)], trip.Seals.Select(q => q.Slot));
        Assert.Equal([Inv(1)], trip.Listings.Select(q => q.Slot));
    }

    [Fact]
    public void Every_other_container_keeps_its_rows_whatever_the_action()
    {
        var trip = TripPartition.Of([Q(Saddle(0), ActionKind.VendorSell), Q(Ret(0), ActionKind.MarketList), Q(SlotRef.Dresser(2), ActionKind.Discard)]);

        Assert.Equal([Saddle(0)], trip.Saddlebag.Select(q => q.Slot));
        Assert.Equal([Ret(0)], trip.Retainers[0xBEEF].Select(q => q.Slot));
        Assert.Equal([SlotRef.Dresser(2)], trip.Dresser.Select(q => q.Slot));
        Assert.Empty(trip.Sells.Concat(trip.Listings).Concat(trip.Here));
    }

    [Fact]
    public void Retainer_rows_are_grouped_by_retainer_in_queue_order()
    {
        var trip = TripPartition.Of([Q(Ret(5, 0xA), ActionKind.Discard), Q(Ret(0, 0xB), ActionKind.Discard), Q(Ret(1, 0xA), ActionKind.VendorSell)]);

        Assert.Equal([0xAul, 0xBul], trip.Retainers.Keys);
        Assert.Equal([Ret(5, 0xA), Ret(1, 0xA)], trip.Retainers[0xA].Select(q => q.Slot));
    }

    [Fact]
    public void Every_ticked_row_goes_to_exactly_one_stop()
    {
        ActionKind[] actions = [ActionKind.Discard, ActionKind.VendorSell, ActionKind.ExpertDelivery, ActionKind.Desynth, ActionKind.MarketList];
        Func<int, SlotRef>[] places = [i => Inv(i), i => Arm(i), i => Saddle(i), i => Ret(i, 0xA), i => Ret(i, 0xB), SlotRef.Dresser];
        var queue = places.SelectMany(place => actions.Select((a, i) => Q(place(i), a))).ToList();

        var trip = TripPartition.Of(queue);
        var stops = trip.Here.Concat(trip.Listings).Concat(trip.Sells).Concat(trip.Seals).Concat(trip.Saddlebag)
            .Concat(trip.Retainers.Values.SelectMany(r => r)).Concat(trip.Dresser).ToList();

        Assert.Equal(queue.Count, stops.Count);
        Assert.True(queue.ToHashSet().SetEquals(stops));
    }

    [Fact]
    public void Ticking_bag_items_never_sends_the_trip_to_the_inn_or_the_saddlebag()
    {
        var trip = TripPartition.Of([Q(Inv(0), ActionKind.Discard), Q(Inv(1), ActionKind.VendorSell), Q(Inv(2), ActionKind.ExpertDelivery)]);

        Assert.False(trip.NeedsInn(visitRetainers: true, visitDresser: true, sweep: false));
        Assert.False(trip.GoesToSaddlebag(openSaddlebag: true, sweep: false));
    }

    [Fact]
    public void Listings_and_retainer_rows_need_the_inn_only_when_retainers_may_be_visited()
    {
        var listings = TripPartition.Of([Q(Inv(0), ActionKind.MarketList)]);
        var retainer = TripPartition.Of([Q(Ret(0), ActionKind.Discard)]);

        Assert.True(listings.NeedsInn(visitRetainers: true, visitDresser: false, sweep: false));
        Assert.False(listings.NeedsInn(visitRetainers: false, visitDresser: true, sweep: false));
        Assert.True(retainer.NeedsInn(visitRetainers: true, visitDresser: false, sweep: false));
        Assert.False(retainer.NeedsInn(visitRetainers: false, visitDresser: true, sweep: false));
    }

    [Fact]
    public void The_dresser_needs_the_inn_only_when_it_may_be_visited()
    {
        var trip = TripPartition.Of([Q(SlotRef.Dresser(0), ActionKind.Discard)]);

        Assert.True(trip.NeedsInn(visitRetainers: false, visitDresser: true, sweep: false));
        Assert.False(trip.NeedsInn(visitRetainers: true, visitDresser: false, sweep: false));
    }

    [Fact]
    public void A_saddlebag_row_goes_to_the_saddlebag_only_when_it_may_be_opened()
    {
        var trip = TripPartition.Of([Q(Saddle(0), ActionKind.Discard)]);

        Assert.True(trip.GoesToSaddlebag(openSaddlebag: true, sweep: false));
        Assert.False(trip.GoesToSaddlebag(openSaddlebag: false, sweep: true));
    }

    [Fact]
    public void A_sweep_goes_wherever_it_is_allowed_and_only_when_unseen_rows_are_cleaned()
    {
        Assert.False(TripPartition.Sweeps(visitContainersWithoutRows: false, UnseenRowsMode.Clean));
        Assert.False(TripPartition.Sweeps(visitContainersWithoutRows: true, UnseenRowsMode.Skip));
        Assert.True(TripPartition.Sweeps(visitContainersWithoutRows: true, UnseenRowsMode.Clean));

        var empty = TripPartition.Of([]);
        Assert.True(empty.GoesToSaddlebag(openSaddlebag: true, sweep: true));
        Assert.True(empty.NeedsInn(visitRetainers: true, visitDresser: false, sweep: true));
        Assert.True(empty.NeedsInn(visitRetainers: false, visitDresser: true, sweep: true));
        Assert.False(empty.NeedsInn(visitRetainers: false, visitDresser: false, sweep: true));
    }

    [Fact]
    public void Items_brought_back_from_a_retainer_finish_from_the_bags()
    {
        var sell = Q(Inv(0), ActionKind.VendorSell, broughtHome: true);
        var seal = Q(Inv(1), ActionKind.ExpertDelivery, broughtHome: true);
        var materia = Q(Inv(2), ActionKind.Discard, broughtHome: true);
        var waitingInBags = Q(Inv(3), ActionKind.Discard);
        var stillWithRetainer = Q(Ret(0), ActionKind.Discard, broughtHome: true);

        var back = TripPartition.BroughtBack([sell, seal, materia, waitingInBags, stillWithRetainer]);
        var (sells, seals, here) = TripPartition.SplitBroughtBack(back);

        Assert.Equal([sell, seal, materia], back);
        Assert.Equal([sell], sells);
        Assert.Equal([seal], seals);
        Assert.Equal([materia], here);
    }

    [Fact]
    public void A_retainers_own_listings_are_kept_apart_from_its_other_rows()
    {
        var discard = Q(Ret(0), ActionKind.Discard);
        var list = Q(Ret(1), ActionKind.MarketList);
        var sell = Q(Ret(2), ActionKind.VendorSell);

        var (rows, own) = TripPartition.SplitRetainer([discard, list, sell]);

        Assert.Equal([discard, sell], rows);
        Assert.Equal([list], own);
    }
}
