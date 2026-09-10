using Gleam.Core.Execution;
using Gleam.Core.Lists;
using Gleam.Core.Logging;
using Gleam.Core.Model;
using Gleam.Core.Planning;
using Gleam.Core.Rules;
using static Gleam.Core.Tests.EndToEnd.E2e;

namespace Gleam.Core.Tests.EndToEnd;

/// <summary>Whole player journeys through the cleaner: review, tick, clean, come back for what waited.</summary>
public class CleanerScenarios
{
    /// <summary>A bag with a bit of everything: junk, obsolete gear, something priceless, currency, a voucher.</summary>
    private static FakeWorld MixedBags()
    {
        var w = new FakeWorld();
        w.Add(Bag(0), 1, 14);           // Allagan Bronze Pieces: vendor junk
        w.Add(Bag(1), 4, 1);            // Aetherial Cotton Doublet: obsolete gear
        w.Add(Bag(2), 5, 1);            // Ironworks Cap: obsolete gear, sells for 500
        w.Add(Bag(3), 18, 3);           // Cordials: outleveled consumable
        w.Add(Bag(4), 9, 1);            // Eternity Ring: unique and untradeable
        w.Add(Bag(5), 10, 1);           // Company Seal Voucher: the game refuses to discard it
        w.Add(Bag(6), 11, 50_000);      // Gil
        w.Add(Bag(7), 2, 5);            // Rarefied lumber: untradeable, worthless, irreplaceable
        return w;
    }

    [Fact]
    public async Task Vendor_preset_sells_what_it_can_discards_the_rest_and_never_touches_the_priceless()
    {
        var world = MixedBags();
        world.ShopOpen = true;
        var log = new MemoryRunLog();

        var plan = Plan(world);

        // The review: the voucher and gil are hidden for good; the ring and the lumber are visible but unticked.
        Assert.Contains(plan.Excluded, e => e.Item.ItemId == 10 && e.IsHardBlock);
        Assert.DoesNotContain(plan.AllRows, r => r.Item.ItemId == 11);
        Assert.False(Row(plan, 9u).Checked);
        Assert.False(Row(plan, 2u).Checked);
        Assert.Equal(ActionKind.VendorSell, Row(plan, 1u).ChosenAction);
        Assert.True(Row(plan, 1u).Checked);
        Assert.True(Row(plan, 4u).Checked);

        var queue = Queue(plan);
        var report = await Clean(world, queue, log);

        Assert.False(report.Aborted);
        Assert.Equal(queue.Count, report.Done);
        Assert.Empty(report.Pending);
        Assert.Equal(queue.Count, log.Entries.Count);
        foreach (var q in queue) Assert.False(world.Has(q.Slot));
        // Untradeable things were discarded, tradeable ones sold; nothing else changed.
        Assert.All(world.Sold, s => Assert.False(Lookup(s.ItemId)!.IsUntradable));
        Assert.True(world.Has(9)); Assert.True(world.Has(10)); Assert.True(world.Has(11)); Assert.True(world.Has(2));
        Assert.Contains("cleaned", report.Summary());
    }

    [Fact]
    public async Task Discard_all_preset_destroys_vendor_value_and_the_soft_cap_notices()
    {
        var world = MixedBags();
        var profile = Profile(PresetName.DiscardAll);
        profile.Thresholds.SoftCapItems = 2;

        var plan = Plan(world, profile);
        var ticked = plan.AllRows.Where(r => r.Checked && r.IsExecutable).ToList();
        Assert.All(ticked, r => Assert.Equal(ActionKind.Discard, r.ChosenAction));
        var cap = SoftCap.Evaluate(plan.AllRows, profile.Thresholds);
        Assert.True(cap.Exceeded);
        Assert.Equal(ticked.Count, cap.Items);

        var report = await Clean(world, Queue(plan));
        Assert.Equal(ticked.Count, report.Done);
        Assert.Empty(world.Sold);
        Assert.Equal(ticked.Count, world.Destroyed.Count);
        Assert.True(plan.Summarize().GilDestroyed > 0);
    }

    [Fact]
    public async Task Selling_waits_for_a_merchant_while_discards_go_ahead_then_finishes_when_one_opens()
    {
        var world = MixedBags();          // no shop, no retainer
        var plan = Plan(world);
        Row(plan, 4u).ChosenAction = ActionKind.Discard;   // the player decides the doublet is not worth carrying to a merchant
        var queue = Queue(plan);
        var sells = queue.Count(q => q.Action == ActionKind.VendorSell);
        var discards = queue.Count(q => q.Action == ActionKind.Discard);
        Assert.True(sells > 0 && discards > 0);

        var first = await Clean(world, queue);
        Assert.Equal(discards, first.Done);
        Assert.Equal(sells, first.Pending.Count);
        Assert.All(first.PendingReasons.Values, r => Assert.Contains("merchant", r));
        Assert.Empty(world.Sold);

        world.ShopOpen = true;
        var second = await Clean(world, Leftover(first));
        Assert.Equal(sells, second.Done);
        Assert.Empty(second.Pending);
        Assert.Equal(sells, world.Sold.Count);
    }

    [Fact]
    public async Task Saddlebag_rows_wait_until_it_is_open_and_the_plan_says_so()
    {
        var world = new FakeWorld();
        world.Add(Bag(0), 1, 14);
        world.Add(Saddlebag(0), 1, 20);
        world.Add(Saddlebag(1), 18, 2);
        world.ShopOpen = true;

        var plan = Plan(world);
        var saddleSection = plan.Sections.Single(s => s.Kind == ContainerKind.Saddlebag);
        Assert.False(saddleSection.IsAvailableNow);
        Assert.Equal(ActionKind.Discard, Row(plan, Saddlebag(0)).ChosenAction);   // only discards happen where the saddlebag sits

        Assert.False(Row(plan, Saddlebag(1)).Checked);                             // cordials: no rule wants them

        var first = await Clean(world, Queue(plan));
        Assert.Equal(1, first.Done);
        Assert.Single(first.Pending);
        Assert.Contains("saddlebag", first.PendingReasons.Values.Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, world.Count(ContainerKind.Saddlebag));

        world.SaddlebagOpen = true;
        var second = await Clean(world, Leftover(first));
        Assert.Equal(1, second.Done);
        Assert.Equal(1, world.Count(ContainerKind.Saddlebag));
        Assert.True(world.Has(Saddlebag(1)));
    }

    [Fact]
    public async Task Retainer_gear_with_materia_comes_home_first_and_is_finished_after_leaving_the_bell()
    {
        var world = new FakeWorld();
        world.Add(Ret(RetA, 0), 5, 1, false, 12, 34);   // Ironworks Cap with two materia, stored with Alpha
        world.Add(Ret(RetA, 1), 1, 7);                   // plain junk next to it
        world.OpenRetainer(RetA);
        var profile = Profile(PresetName.DiscardAll);

        var plan = Plan(world, profile);
        var cap = Row(plan, 5u);
        Assert.False(cap.Checked);                                                   // materia on it: the review asks first
        Assert.Contains(cap.Proposal.Warnings, w => w.StartsWith("Retrieve materia first"));
        cap.Checked = true;
        var first = await Clean(world, Queue(plan));

        // The junk is discarded on the spot; the cap is brought home because its materia cannot come off at the bell.
        Assert.Equal(1, first.Done);
        var follow = Assert.Single(first.Moved);
        Assert.True(follow.BroughtHome);
        Assert.True(follow.RetrieveMateriaFirst);
        Assert.Equal(ContainerKind.Inventory, follow.Kind);
        Assert.Equal(1, world.Count(ContainerKind.Inventory));
        Assert.Contains("brought back", first.Summary());

        // Still at the bell: the follow-up must wait, nothing is destroyed with materia on it.
        var atBell = await Clean(world, Leftover(first));
        Assert.Equal(0, atBell.Done);
        Assert.Single(atBell.Pending);
        Assert.Contains("retainer is summoned", atBell.PendingReasons.Values.Single());

        world.LeaveBell();
        var home = await Clean(world, Leftover(atBell));
        Assert.Equal(1, home.Done);
        Assert.Equal(2, world.CountOf(FakeWorld.MateriaItemId));     // both materia are in the bags
        Assert.False(world.Has(5));
        Assert.Single(world.Destroyed, d => d.ItemId == 5);
    }

    [Fact]
    public async Task Expert_delivery_from_a_retainer_travels_home_then_turns_in_at_the_grand_company()
    {
        var world = new FakeWorld();
        world.Add(Ret(RetA, 0), 4, 1);
        world.OpenRetainer(RetA);

        var plan = Plan(world);
        var row = Row(plan, 4u);
        Assert.Contains(ActionKind.ExpertDelivery, row.Proposal.Alternatives.Append(row.Proposal.Action));
        row.ChosenAction = ActionKind.ExpertDelivery;
        row.Checked = true;

        var first = await Clean(world, Queue(plan));
        var follow = Assert.Single(first.Moved);
        Assert.Equal(ActionKind.ExpertDelivery, follow.Action);
        Assert.Equal(ContainerKind.Inventory, follow.Kind);

        world.LeaveBell();
        var waiting = await Clean(world, Leftover(first));
        Assert.Single(waiting.Pending);
        Assert.Contains("Grand Company", waiting.PendingReasons.Values.Single());

        world.GrandCompanyOpen = true;
        var done = await Clean(world, Leftover(waiting));
        Assert.Equal(1, done.Done);
        Assert.Single(world.TurnedIn, t => t.ItemId == 4);
    }

    [Fact]
    public async Task Market_preset_lists_at_the_home_world_price_and_spills_to_the_next_retainer_when_slots_run_out()
    {
        var world = new FakeWorld { MarketCapacity = 1 };
        world.Add(Bag(0), 6, 1);      // Woolen Coat, marketable
        world.Add(Bag(1), 12, 5);     // Expensive Potions, marketable, five in the stack
        world.Add(Bag(2), 1, 14);     // vendor junk: not marketable, sold to the retainer instead
        world.OpenRetainer(RetA);
        var market = new Dictionary<uint, MarketPrice>
        {
            [6] = new(6, 3_000, 0, DateTimeOffset.UtcNow),
            [12] = new(12, 400, 0, DateTimeOffset.UtcNow),
        };

        var plan = Plan(world, Profile(PresetName.MarketBoard), Context(market: market));
        Assert.Equal(ActionKind.MarketList, Row(plan, 6u).ChosenAction);
        Assert.True(Row(plan, 6u).Checked);                                          // a sure thing starts ticked
        Assert.Equal(ActionKind.MarketList, Row(plan, 12u).ChosenAction);
        Assert.False(Row(plan, 12u).Checked);                                        // medium confidence waits for the player
        Row(plan, 12u).Checked = true;
        Assert.Equal(ActionKind.VendorSell, Row(plan, 1u).ChosenAction);
        Assert.Equal(400, Row(plan, 12u).Proposal.MarketUnitPrice);

        // Listings go up two at a time; Alpha has room for exactly one listing.
        var options = new ExecutionOptions { MarketStackSize = 2 };
        var first = await Clean(world, Queue(plan), options: options);
        Assert.Single(world.Listings, l => l.Retainer == RetA);
        Assert.Contains(world.Sold, s => s.ItemId == 1);
        var leftover = Leftover(first);
        Assert.NotEmpty(leftover);
        Assert.All(leftover, q => Assert.Equal(ActionKind.MarketList, q.Action));

        // Bravo takes the rest, piece by piece, at the same price.
        world.LeaveBell();
        world.OpenRetainer(RetB);
        world.MarketCapacity = 20;
        var second = await Clean(world, leftover, options: options);
        Assert.Empty(second.Pending);
        Assert.Empty(second.Moved);
        Assert.False(world.Has(6)); Assert.False(world.Has(12));
        Assert.Equal(5, world.Listings.Where(l => l.ItemId == 12).Sum(l => l.Quantity));
        Assert.All(world.Listings.Where(l => l.ItemId == 12), l => { Assert.Equal(400, l.UnitPrice); Assert.True(l.Quantity <= 2); });
        Assert.Equal(3_000, world.Listings.Single(l => l.ItemId == 6).UnitPrice);
    }

    [Fact]
    public async Task Never_touch_always_clean_registered_spares_and_session_skips_shape_the_review()
    {
        var world = new FakeWorld();
        world.Add(Bag(0), 1, 14);      // would be junk, but is on Never touch
        world.Add(Bag(1), 15, 30);     // not junk by any rule, but on Always clean
        world.Add(Bag(2), 20, 1);      // Wind-up spare minion, already registered
        world.Add(Bag(3), 21, 1);      // Orchestrion roll, not registered
        world.Add(Bag(4), 4, 1);       // obsolete gear the player skips this time
        world.ShopOpen = true;

        var never = new ItemList(); never.Add(1);
        var always = new ItemList(); always.Add(15);
        var registered = new Dictionary<uint, bool> { [20] = true, [21] = false };
        var firstLook = Plan(world, ctx: Context(registered: registered), neverTouch: never, alwaysClean: always);
        var skips = new HashSet<string> { Row(firstLook, 4u).Key };
        var plan = Plan(world, ctx: Context(registered: registered), neverTouch: never, alwaysClean: always, skips: skips);

        Assert.Contains(plan.Excluded, e => e.Item.ItemId == 1 && !e.IsHardBlock);
        Assert.Equal(Confidence.User, Row(plan, 15u).Proposal.Confidence);
        Assert.True(Row(plan, 15u).Checked);
        Assert.True(Row(plan, 20u).Proposal.Registered);
        Assert.NotEqual("manual", Row(plan, 20u).Proposal.RuleId);
        Assert.False(Row(plan, 21u).Checked);                               // unregistered: hand-pick only
        Assert.False(Row(plan, 4u).Checked);                                // skipped this session

        var report = await Clean(world, Queue(plan));
        Assert.True(world.Has(1)); Assert.True(world.Has(4)); Assert.True(world.Has(21));
        Assert.False(world.Has(15)); Assert.False(world.Has(20));
        Assert.Equal(report.Done, world.Sold.Count + world.Destroyed.Count);
    }

    [Fact]
    public async Task Large_stacks_and_protected_items_never_reach_the_queue()
    {
        var world = new FakeWorld();
        world.Add(Bag(0), 1, 250);               // a hoard of vendor junk
        world.Add(Bag(1), 1, 14);                // a normal stack of the same
        world.Add(Bag(2), UltimateToken, 1);
        world.ShopOpen = true;

        var plan = Plan(world, ctx: Context(protectedIds: [UltimateToken]));
        Assert.Contains(plan.Excluded, e => e.Item.ItemId == UltimateToken && e.IsHardBlock);
        var hoard = Row(plan, Bag(0));
        Assert.False(hoard.Checked);
        Assert.Equal("manual", hoard.Proposal.RuleId);
        Assert.Contains(hoard.Proposal.Warnings, w => w.StartsWith("Stack of 250"));
        Assert.True(Row(plan, Bag(1)).Checked);

        var report = await Clean(world, Queue(plan));
        Assert.Equal(1, report.Done);
        Assert.Equal(250, world.CountOf(1));
        Assert.True(world.Has(UltimateToken));
    }

    [Fact]
    public async Task Dresser_items_are_restored_into_free_bag_space_before_they_are_discarded()
    {
        var world = new FakeWorld { DresserOpen = true, ShopOpen = true };
        world.Add(Dresser(3), 4, 1);
        for (var i = 0; i < 140; i++) world.Add(Bag(i % 35, (uint)(i / 35)), 11, 1);   // bags full of gil stacks

        var plan = Plan(world, ctx: Context());
        var row = Row(plan, 4u);
        Assert.True(row.IsExecutable);
        row.Checked = true;

        var blocked = await Clean(world, Queue(plan));
        Assert.Single(blocked.Pending);
        Assert.Contains("bag space", blocked.PendingReasons.Values.Single());
        Assert.True(world.Has(Dresser(3)));

        world.Slots.Remove(Bag(0));
        var done = await Clean(world, Leftover(blocked));
        Assert.Equal(1, done.Done);
        Assert.False(world.Has(Dresser(3)));
        Assert.Contains(world.Calls, c => c.StartsWith("restore:"));
        Assert.Single(world.Destroyed, d => d.ItemId == 4);                          // dresser copies are discarded, never sold
    }
}
