using Gleam.Core.Execution;
using Gleam.Core.Lists;
using Gleam.Core.Model;
using Gleam.Core.Planning;
using Gleam.Core.Rules;
using static Gleam.Core.Tests.TestData;

namespace Gleam.Core.Tests;

/// <summary>
/// The item-safety fixes from the September 2026 review. Each test is a way Gleam could lose, or undersell, something
/// the player wanted to keep.
/// </summary>
public class SafetyFixTests
{
    // An event apron: untradeable, not unique, level 1, every class, and no vendor sells it back.
    private static readonly ItemInfo Apron = ItemInfo.Test(900, "Valentione Apron", vendor: 0, marketable: false, equipment: true,
        levelEquip: 1, ilvl: 1, cjc: CjcAll, category: "Body", untradable: true);

    private static readonly ItemInfo ShopGear = ItemInfo.Test(901, "Untradeable Shop Coat", vendor: 5, marketable: false, equipment: true,
        levelEquip: 1, ilvl: 1, cjc: CjcAll, category: "Body", untradable: true, vendorBuyable: true);

    private static readonly ItemInfo TomeGear = ItemInfo.Test(902, "Old Tome Coat", vendor: 300, marketable: false, equipment: true,
        levelEquip: 60, ilvl: 270, rarity: 3, cjc: CjcAll, category: "Body", untradable: true);

    private static ItemInfo? LookupWithGear(uint id) => id switch
    {
        900 => Apron,
        901 => ShopGear,
        902 => TomeGear,
        _ => Lookup(id),
    };

    private static PlannerInputs Inputs(ItemContext? ctx = null) => new()
    {
        IncludeUnproposed = true,
        Context = ctx ?? Context(),
        Profile = MakeProfile(),
        InfoLookup = LookupWithGear,
        ProtectList = new ItemList(),
        AlwaysDiscardList = new ItemList(),
        SessionSkips = new HashSet<string>(),
        IsAvailable = (_, _) => true,
        RetainerNames = new Dictionary<ulong, string>(),
    };

    private static PlanRow Row(SlotRef slot, uint itemId = 1, bool ticked = true, ActionKind action = ActionKind.Discard) => new()
    {
        Proposal = new Proposal
        {
            Item = ScannedItem.Simple(slot, itemId, 1), Info = Items[itemId], Action = action, Confidence = Confidence.High,
            RuleId = VendorOnlyJunkRule.RuleId, Reason = "test",
        },
        Checked = ticked,
        ChosenAction = action,
    };

    private static RunPlan Plan(params PlanRow[] rows)
    {
        var plan = new RunPlan { CharacterId = 1, CharacterName = "Someone" };
        var section = new PlanSection { Kind = rows[0].Item.Slot.Kind, OwnerId = rows[0].Item.Slot.OwnerId, OwnerName = string.Empty };
        section.Rows.AddRange(rows);
        plan.Sections.Add(section);
        return plan;
    }

    // ---------- untradeable gear that cannot be bought back ----------

    [Fact]
    public void Untradeable_gear_no_vendor_sells_is_kept_from_the_rules_but_can_be_picked_by_hand()
    {
        var reason = HardBlocks.Check(ScannedItem.Simple(Arm(0), 900, 1), Apron, Context());

        Assert.Equal(HardBlockReason.UntradeableGear, reason);
        Assert.False(HardBlocks.IsImmovable(reason));
    }

    [Fact]
    public void Untradeable_gear_a_vendor_sells_or_bought_with_a_retired_currency_is_left_to_the_rules()
    {
        Assert.Equal(HardBlockReason.None, HardBlocks.Check(ScannedItem.Simple(Arm(0), 901, 1), ShopGear, Context()));
        Assert.Equal(HardBlockReason.None, HardBlocks.Check(ScannedItem.Simple(Arm(1), 902, 1), TomeGear, Context(retiredGear: [902u])));
    }

    [Fact]
    public void A_gear_set_piece_stays_untouchable_even_when_it_is_untradeable()
    {
        var reason = HardBlocks.Check(ScannedItem.Simple(Arm(0), 900, 1), Apron, Context(gearsetItems: [900u]));

        Assert.Equal(HardBlockReason.InGearset, reason);
        Assert.True(HardBlocks.IsImmovable(reason));
    }

    [Fact]
    public void The_event_apron_is_no_longer_ticked_for_discard_as_obsolete_gear()
    {
        var plan = new RunPlanner().Build([ScannedItem.Simple(Arm(0), 900, 1)], Inputs());

        var row = Assert.Single(plan.AllRows);
        Assert.False(row.IsSuggested);
        Assert.False(row.Checked);
        Assert.Contains(row.Proposal.Warnings, w => w.StartsWith("Untradeable gear", StringComparison.Ordinal));
    }

    [Fact]
    public void Retired_currency_gear_stays_when_no_gear_sets_were_read()
    {
        var rule = new RetiredCurrencyGearRule();

        var proposal = rule.Evaluate(ScannedItem.Simple(Arm(0), 14, 1), Items[14], Context(retiredGear: [14u], maxGearsetIlvl: 0), new Thresholds());

        Assert.Null(proposal);
    }

    // ---------- market prices ----------

    [Fact]
    public void An_hq_item_with_no_hq_listings_has_no_price_rather_than_the_nq_one()
    {
        var price = new MarketPrice(12, MinNq: 100, MinHq: 0, DateTimeOffset.UnixEpoch);

        Assert.Equal(0, price.MinFor(hq: true));
        Assert.Equal(100, price.MinFor(hq: false));
    }

    [Fact]
    public void An_item_with_no_listings_counts_as_price_unknown_and_starts_unticked()
    {
        var noListings = new Dictionary<uint, MarketPrice> { [12] = new(12, 0, 0, DateTimeOffset.UnixEpoch) };
        var ctx = Context().WithMarket(noListings, attempted: true, new Dictionary<uint, bool>());
        var sale = new Proposal
        {
            Item = ScannedItem.Simple(Inv(0), 12, 3), Info = Items[12], Action = ActionKind.VendorSell,
            Confidence = Confidence.High, RuleId = "test", Reason = "test",
        };

        var result = MarketPricePostProcessor.Apply(sale, ctx, new Thresholds());

        Assert.Contains("Market price unavailable", result.Warnings);
        Assert.False(result.DefaultChecked);
    }

    [Fact]
    public void A_registered_spare_that_sells_on_the_market_waits_for_a_look()
    {
        var ctx = Context(registered: new Dictionary<uint, bool> { [21] = true });

        var proposal = new RegisteredDuplicateRule().Evaluate(ScannedItem.Simple(Inv(0), 21, 1), Items[21], ctx, new Thresholds());

        Assert.NotNull(proposal);
        Assert.Contains("Sellable on the market", proposal!.Warnings);
        Assert.False(proposal.DefaultChecked);
    }

    // ---------- the player's unticks ----------

    [Fact]
    public void An_untick_follows_the_item_to_its_new_slot_so_the_after_ventures_clean_leaves_it()
    {
        var unticked = Row(Inv(0), ticked: false);
        var before = Plan(unticked);
        var skips = new HashSet<string> { unticked.Key };
        // The same stack one slot along after the sort, ticked again by the planner.
        var afterSort = Row(Inv(1), ticked: true);
        var fresh = Plan(afterSort);

        RescanTicks.CarryOver(before, fresh, userAsked: false);
        foreach (var key in RescanTicks.CarrySkips(before, fresh, skips)) skips.Add(key);

        Assert.False(afterSort.Checked);
        Assert.Empty(UnattendedSelection.PickAfterVentures([afterSort], skips));
    }

    [Fact]
    public void A_ticked_item_that_moved_is_not_made_a_skip()
    {
        var ticked = Row(Inv(0), ticked: true);
        var fresh = Plan(Row(Inv(1), ticked: true));

        Assert.Empty(RescanTicks.CarrySkips(Plan(ticked), fresh, new HashSet<string> { "some other row" }));
    }

    // ---------- hands-free and after ventures ----------

    [Fact]
    public void Nothing_untradeable_is_discarded_unseen()
    {
        var none = new HashSet<string>();
        var nothingReviewed = new HashSet<(ContainerKind, ulong, uint, bool)>();

        Assert.False(UnattendedSelection.IsUnseenPick(Row(Ret(0), itemId: 8), none, nothingReviewed));
        Assert.True(UnattendedSelection.IsUnseenPick(Row(Ret(1), itemId: 1), none, nothingReviewed));
    }

    [Fact]
    public void An_unattended_batch_stops_at_the_big_run_limit()
    {
        var pick = UnattendedSelection.Capped(_ => true, new Thresholds { SoftCapItems = 2, SoftCapGil = 1_000_000 });

        var queue = PlanQueue.Build([Row(Inv(0)), Row(Inv(1)), Row(Inv(2))], pick, requireChecked: false);

        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public void An_unattended_batch_also_stops_at_the_gil_limit()
    {
        // Allagan Bronze Piece sells for 28 gil a piece; the third would go over 60.
        var pick = UnattendedSelection.Capped(_ => true, new Thresholds { SoftCapItems = 100, SoftCapGil = 60 });

        var queue = PlanQueue.Build([Row(Inv(0)), Row(Inv(1)), Row(Inv(2))], pick, requireChecked: false);

        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public void A_listing_brought_home_keeps_waiting_instead_of_disappearing()
    {
        var listing = new QueuedAction(Inv(0), 1, 1, false, ActionKind.MarketList, false, "item", 0, "r", BroughtHome: true);
        var sale = listing with { Slot = Inv(1), Action = ActionKind.VendorSell };

        Assert.Equal([sale], TripPartition.BroughtBack([listing, sale]));
    }

    // ---------- lists ----------

    [Fact]
    public void Removing_an_entry_for_one_character_keeps_another_characters_own()
    {
        var list = new ItemList();
        list.Add(5);
        list.Add(5, characterId: 1);
        list.Add(5, characterId: 2);

        list.RemoveFor(5, characterId: 1);

        var left = Assert.Single(list.Entries);
        Assert.Equal(2ul, left.CharacterId);
    }

    // ---------- confirmation prompts ----------

    [Theory]
    [InlineData("Discard 3 hi-potions?", "Potion", false)]
    [InlineData("Discard the steak?", "Tea", false)]
    [InlineData("Discard 3 hi-potions?", "Hi-Potion", true)]
    [InlineData("Discard 121 grade 2 gemdraughts of intelligence?", "Grade 2 Gemdraught of Intelligence", true)]
    [InlineData("Discard 986 magicked prisms (sunshine)?", "Magicked Prism (Sunshine)", true)]
    [InlineData("Discard 2 gem­draughts?", "Gemdraught", true)]
    [InlineData("「ハイポーション」を捨てます。", "ハイポーション", true)]
    public void A_prompt_names_the_item_only_in_whole_words(string prompt, string name, bool expected) =>
        Assert.Equal(expected, PromptMatch.Mentions(prompt, name));
}
