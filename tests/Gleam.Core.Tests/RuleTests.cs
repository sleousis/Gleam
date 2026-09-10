using Gleam.Core.Lists;
using Gleam.Core.Planning;
using Gleam.Core.Model;
using Gleam.Core.Rules;
using static Gleam.Core.Tests.TestData;

namespace Gleam.Core.Tests;

public class RuleTests
{
    private readonly Thresholds t = Presets.For(PresetName.Vendor);

    [Fact]
    public void VendorOnlyJunk_proposes_sell_for_cheap_untradeable_with_vendor_price()
    {
        var item = ScannedItem.Simple(Inv(0), 1, 14);
        var p = new VendorOnlyJunkRule().Evaluate(item, Items[1], Context(), t);

        Assert.NotNull(p);
        Assert.Equal(ActionKind.VendorSell, p!.Action);
        Assert.Equal(28 * 14, p.ValueGil);
        Assert.Equal(Confidence.High, p.Confidence);
        Assert.Contains(ActionKind.Discard, p.Alternatives);
    }

    [Fact]
    public void VendorOnlyJunk_ignores_items_above_unit_price_threshold()
    {
        var pricey = ItemInfo.Test(99, "Pricey", vendor: 5000, marketable: false);
        var p = new VendorOnlyJunkRule().Evaluate(ScannedItem.Simple(Inv(0), 99, 1), pricey, Context(), t);
        Assert.Null(p);
    }

    [Fact]
    public void VendorOnlyJunk_never_proposes_untradeable_items_with_no_vendor_value()
    {
        // A Fantasia has exactly this shape. Only the game's own vendor price makes something junk.
        Assert.Null(new VendorOnlyJunkRule().Evaluate(ScannedItem.Simple(Inv(0), 2, 3), Items[2], Context(), t));
        Assert.Null(new VendorOnlyJunkRule().Evaluate(ScannedItem.Simple(Inv(0), 16, 1), Items[16], Context(), t));
    }

    [Fact]
    public void VendorOnlyJunk_stays_quiet_when_a_recipe_still_uses_the_material()
    {
        // Copper Ore is a material with a vendor price, so only the recipe decides which rule owns it.
        var ctx = Context(recipes: id => id == 7 ? [new RecipeUse(JobCrp, 80)] : []);
        Assert.Null(new VendorOnlyJunkRule().Evaluate(ScannedItem.Simple(Inv(0), 7, 50), Items[7], ctx, t));
    }

    [Fact]
    public void VendorOnlyJunk_keeps_a_material_that_no_recipe_uses()
    {
        // The gap this closes: an untradeable dye is a "material" the crafting rule skips because no recipe
        // touches it, and the vendor rule used to skip every material. It reached the player unproposed.
        var p = new VendorOnlyJunkRule().Evaluate(ScannedItem.Simple(Inv(0), 22, 9), Items[22], Context(), t);

        Assert.NotNull(p);
        Assert.Equal(ActionKind.VendorSell, p!.Action);
        Assert.Equal(9, p.ValueGil);
    }

    [Fact]
    public void The_two_material_rules_never_both_claim_the_same_item()
    {
        // One owns materials a recipe uses, the other owns the rest. Neither may leave a gap or overlap.
        foreach (var recipes in new Func<uint, IReadOnlyList<RecipeUse>>[]
                 {
                     _ => [],
                     id => id == 7 ? [new RecipeUse(JobCrp, 5)] : [],
                     id => id == 7 ? [new RecipeUse(JobCrp, 85)] : [],
                 })
        {
            var ctx = Context(recipes: recipes);
            var item = ScannedItem.Simple(Inv(0), 7, 50);
            var vendor = new VendorOnlyJunkRule().Evaluate(item, Items[7], ctx, t);
            var craft = new UnusableCraftingMatsRule().Evaluate(item, Items[7], ctx, t);
            Assert.False(vendor is not null && craft is not null, "both rules claimed the same material");
        }
    }

    [Fact]
    public void VendorOnlyJunk_leaves_usable_items_alone_even_with_a_vendor_price()
    {
        // 97 Priority Aetheryte Passes are 9,700g of "junk" to a price check and free teleports to a player.
        Assert.Null(new VendorOnlyJunkRule().Evaluate(ScannedItem.Simple(Inv(0), 17, 97), Items[17], Context(), t));
    }

    [Fact]
    public void OutleveledConsumables_ignores_medicine_with_no_level_requirement()
    {
        Assert.Null(new OutleveledConsumablesRule().Evaluate(ScannedItem.Simple(Inv(0), 18, 26), Items[18], Context(maxGearsetIlvl: 700), t));
        Assert.NotNull(new OutleveledConsumablesRule().Evaluate(ScannedItem.Simple(Inv(0), 19, 5), Items[19], Context(maxGearsetIlvl: 700), t));
    }

    [Fact]
    public void VendorOnlyJunk_never_touches_marketable_or_equipment()
    {
        Assert.Null(new VendorOnlyJunkRule().Evaluate(ScannedItem.Simple(Inv(0), 15, 1), Items[15], Context(), t));
        Assert.Null(new VendorOnlyJunkRule().Evaluate(ScannedItem.Simple(Inv(0), 4, 1), Items[4], Context(), t));
    }

    [Fact]
    public void ObsoleteGear_fires_when_best_job_is_far_above_equip_level()
    {
        var p = new ObsoleteGearRule().Evaluate(ScannedItem.Simple(Arm(0), 4, 1), Items[4], Context(), t);
        Assert.NotNull(p);
        Assert.Equal(ActionKind.ExpertDelivery, p!.Action); // rarity 2 → seals first
        Assert.Contains(ActionKind.Desynth, p.Alternatives);
        Assert.Contains(ActionKind.VendorSell, p.Alternatives);
        Assert.Contains(ActionKind.Discard, p.Alternatives);
        Assert.Empty(p.Warnings);
        Assert.True(p.DefaultChecked);
    }

    [Fact]
    public void ObsoleteGear_respects_gap_per_job_category()
    {
        // BLM is 100, cap is lv50 → gap 50 ≥ 15 → fires
        Assert.NotNull(new ObsoleteGearRule().Evaluate(ScannedItem.Simple(Arm(0), 5, 1), Items[5], Context(), t));

        // With a huge gap requirement it stays quiet
        var strict = Presets.For(PresetName.Vendor);
        strict.ObsoleteGearLevelGap = 60;
        Assert.Null(new ObsoleteGearRule().Evaluate(ScannedItem.Simple(Arm(0), 5, 1), Items[5], Context(), strict));
    }

    [Fact]
    public void ObsoleteGear_skips_gearset_items_and_unplayed_jobs_by_default()
    {
        Assert.Null(new ObsoleteGearRule().Evaluate(ScannedItem.Simple(Arm(0), 4, 1), Items[4], Context(gearsetItems: [4u]), t));
        Assert.Null(new ObsoleteGearRule().Evaluate(ScannedItem.Simple(Arm(0), 13, 1), Items[13], Context(), t));

        var aggressive = Presets.For(PresetName.DiscardAll);
        var p = new ObsoleteGearRule().Evaluate(ScannedItem.Simple(Arm(0), 13, 1), Items[13], Context(), aggressive);
        Assert.NotNull(p);
        Assert.Equal(Confidence.Medium, p!.Confidence);
    }

    [Fact]
    public void ObsoleteGear_warns_about_materia_and_starts_unchecked()
    {
        var item = WithMateria(ScannedItem.Simple(Arm(0), 4, 1), 12, 34);
        var p = new ObsoleteGearRule().Evaluate(item, Items[4], Context(), t);
        Assert.NotNull(p);
        Assert.Contains(p!.Warnings, w => w.Contains("materia", StringComparison.OrdinalIgnoreCase));
        Assert.False(p.DefaultChecked);
        Assert.True(p.NeedsMateriaRetrieval);
    }

    [Fact]
    public void DresserZeroPlates_only_fires_with_loaded_plates_and_no_reference()
    {
        var dresser = ScannedItem.Simple(SlotRef.Dresser(7), 6, 1);
        Assert.NotNull(new DresserZeroPlatesRule().Evaluate(dresser, Items[6], Context(platesLoaded: true), t));
        Assert.Null(new DresserZeroPlatesRule().Evaluate(dresser, Items[6], Context(platesLoaded: false), t));
        Assert.Null(new DresserZeroPlatesRule().Evaluate(dresser, Items[6], Context(plateItems: [6u]), t));
        Assert.Null(new DresserZeroPlatesRule().Evaluate(ScannedItem.Simple(Inv(0), 6, 1), Items[6], Context(), t));
    }

    [Fact]
    public void DresserZeroPlates_flags_dye_loss_as_a_warning()
    {
        var dyed = ScannedItem.Simple(SlotRef.Dresser(7), 6, 1) with { Stain0 = 3 };
        var p = new DresserZeroPlatesRule().Evaluate(dyed, Items[6], Context(), t);
        Assert.NotNull(p);
        Assert.Contains("Dye is lost", p!.Warnings);
    }

    [Fact]
    public void OutleveledConsumables_uses_gearset_item_level_as_yardstick()
    {
        var p = new OutleveledConsumablesRule().Evaluate(ScannedItem.Simple(Inv(0), 3, 9), Items[3], Context(maxGearsetIlvl: 700), t);
        Assert.NotNull(p);
        Assert.Equal(ActionKind.VendorSell, p!.Action);
        Assert.Equal(45 * 9, p.ValueGil);

        Assert.Null(new OutleveledConsumablesRule().Evaluate(ScannedItem.Simple(Inv(0), 3, 9), Items[3], Context(maxGearsetIlvl: 200), t));
        Assert.Null(new OutleveledConsumablesRule().Evaluate(ScannedItem.Simple(Inv(0), 3, 9), Items[3], Context(maxGearsetIlvl: 0), t));
    }

    [Fact]
    public void OutleveledConsumables_marks_marketable_ones_medium_with_warning()
    {
        var p = new OutleveledConsumablesRule().Evaluate(ScannedItem.Simple(Inv(0), 12, 1), Items[12], Context(), t);
        Assert.NotNull(p);
        Assert.Equal(Confidence.Medium, p!.Confidence);
        Assert.False(p.DefaultChecked);
    }

    [Fact]
    public void UnusableCraftingMats_requires_all_recipes_low_and_crafters_far_past()
    {
        var lowOnly = Context(recipes: id => id == 7 ? [new RecipeUse(JobCrp, 5), new RecipeUse(JobWvr, 8)] : []);
        var p = new UnusableCraftingMatsRule().Evaluate(ScannedItem.Simple(Inv(0), 7, 50), Items[7], lowOnly, t);
        Assert.NotNull(p);
        Assert.Equal(ActionKind.VendorSell, p!.Action);
        Assert.Equal(50, p.ValueGil);

        // WVR is only 20; a lv15 WVR recipe is not "far past" with a 10-level lead requirement
        var borderline = Context(recipes: id => id == 7 ? [new RecipeUse(JobWvr, 15)] : []);
        Assert.Null(new UnusableCraftingMatsRule().Evaluate(ScannedItem.Simple(Inv(0), 7, 50), Items[7], borderline, t));

        var highRecipe = Context(recipes: id => id == 7 ? [new RecipeUse(JobCrp, 85)] : []);
        Assert.Null(new UnusableCraftingMatsRule().Evaluate(ScannedItem.Simple(Inv(0), 7, 50), Items[7], highRecipe, t));
    }

    [Fact]
    public void SeasonalItems_are_low_confidence_and_unchecked()
    {
        var p = new SeasonalItemsRule().Evaluate(ScannedItem.Simple(Inv(0), 8, 2), Items[8], Context(seasonal: [8u]), t);
        Assert.NotNull(p);
        Assert.Equal(Confidence.Low, p!.Confidence);
        Assert.False(p.DefaultChecked);
        Assert.Null(new SeasonalItemsRule().Evaluate(ScannedItem.Simple(Inv(0), 8, 2), Items[8], Context(), t));
    }

    [Fact]
    public void RetiredCurrencyGear_fires_below_current_tier_and_outside_gearsets()
    {
        var ctx = Context(retiredGear: [14u], maxGearsetIlvl: 700);
        var p = new RetiredCurrencyGearRule().Evaluate(ScannedItem.Simple(Arm(0), 14, 1), Items[14], ctx, t);
        Assert.NotNull(p);
        Assert.Equal(ActionKind.ExpertDelivery, p!.Action);

        Assert.Null(new RetiredCurrencyGearRule().Evaluate(ScannedItem.Simple(Arm(0), 14, 1), Items[14], Context(retiredGear: [14u], gearsetItems: [14u]), t));
        Assert.Null(new RetiredCurrencyGearRule().Evaluate(ScannedItem.Simple(Arm(0), 14, 1), Items[14], Context(retiredGear: [14u], maxGearsetIlvl: 100), t));
    }

    [Fact]
    public void MarketPostProcessor_warns_but_keeps_the_action_when_market_clearly_wins()
    {
        var market = new Dictionary<uint, MarketPrice> { [12] = new(12, 20_000, 0, DateTimeOffset.UtcNow) };
        var ctx = Context(market: market);
        var proposal = new OutleveledConsumablesRule().Evaluate(ScannedItem.Simple(Inv(0), 12, 1), Items[12], ctx, t)!;

        var result = MarketPricePostProcessor.Apply(proposal, ctx, t);
        Assert.True(result.Action.IsDestructive());          // no more "show only": the preset decides
        Assert.False(result.DefaultChecked);                 // but it starts unchecked
        Assert.Contains(result.Warnings, w => w.Contains("19,000g")); // 20,000 × (1 - 0.05)
        Assert.Contains(result.Warnings, w => w.Contains("market board", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MarketPostProcessor_upgrades_discard_to_vendor_sell_when_vendor_pays()
    {
        var proposal = new Proposal
        {
            Item = ScannedItem.Simple(Inv(0), 12, 2), Info = Items[12], Action = ActionKind.Discard,
            Confidence = Confidence.High, RuleId = "x", Reason = "r",
        };
        var result = MarketPricePostProcessor.Apply(proposal, Context(), t);
        Assert.Equal(ActionKind.VendorSell, result.Action);
        Assert.Equal(100, result.ValueGil);
        Assert.Contains(ActionKind.Discard, result.Alternatives);
    }

    [Fact]
    public void MarketPostProcessor_flags_marketable_items_whose_price_could_not_be_fetched()
    {
        var ctx = new ItemContext { MarketLookupAttempted = true, JobLevels = Context().JobLevels, MaxGearsetItemLevel = 700 };
        var proposal = new Proposal
        {
            Item = ScannedItem.Simple(Inv(0), 15, 1), Info = Items[15], Action = ActionKind.VendorSell,
            Confidence = Confidence.High, RuleId = "x", Reason = "r",
        };
        var flagged = MarketPricePostProcessor.Apply(proposal, ctx, t);
        Assert.Contains("Market price unavailable", flagged.Warnings);
        Assert.False(flagged.DefaultChecked);

        var notAttempted = MarketPricePostProcessor.Apply(proposal, Context(), t);
        Assert.Empty(notAttempted.Warnings);
    }

    [Fact]
    public void MarketPostProcessor_leaves_user_forced_rows_alone()
    {
        var market = new Dictionary<uint, MarketPrice> { [12] = new(12, 1_000_000, 0, DateTimeOffset.UtcNow) };
        var proposal = new Proposal
        {
            Item = ScannedItem.Simple(Inv(0), 12, 1), Info = Items[12], Action = ActionKind.Discard,
            Confidence = Confidence.User, RuleId = "always-discard", Reason = "user",
        };
        var result = MarketPricePostProcessor.Apply(proposal, Context(market: market), t);
        Assert.Equal(ActionKind.Discard, result.Action);
    }

    [Fact]
    public void RuleEngine_keeps_the_most_confident_rule_and_merges_alternatives()
    {
        var engine = new RuleEngine();
        var items = new[] { ScannedItem.Simple(Inv(0), 3, 9) };
        var proposals = engine.Evaluate(items, Lookup, Context(), t);
        Assert.Single(proposals);
        Assert.Equal(OutleveledConsumablesRule.RuleId, proposals[0].RuleId);
    }

    [Fact]
    public void RuleEngine_honours_the_enabled_rule_set()
    {
        var engine = new RuleEngine();
        var items = new[] { ScannedItem.Simple(Inv(0), 1, 14) };
        Assert.Single(engine.Evaluate(items, Lookup, Context(), t));
        Assert.Empty(engine.Evaluate(items, Lookup, Context(), t, new HashSet<string> { ObsoleteGearRule.RuleId }));
    }

    [Fact]
    public void Registered_collectibles_are_proposed_and_unregistered_ones_are_not()
    {
        var ctx = Context(registered: new Dictionary<uint, bool> { [20] = true, [21] = false });
        var rule = new RegisteredDuplicateRule();

        var spare = rule.Evaluate(ScannedItem.Simple(Inv(0), 20, 1), Items[20], ctx, t);
        Assert.NotNull(spare);
        Assert.Equal(ActionKind.Discard, spare!.Action);
        Assert.Equal(Confidence.High, spare.Confidence);

        Assert.Null(rule.Evaluate(ScannedItem.Simple(Inv(1), 21, 2), Items[21], ctx, t));

        // The hard blocks step aside for a registered spare, even a unique untradeable minion.
        Assert.Equal(HardBlockReason.None, HardBlocks.Check(ScannedItem.Simple(Inv(0), 20, 1), Items[20], ctx));
        Assert.Equal(HardBlockReason.NeverProposedCategory, HardBlocks.Check(ScannedItem.Simple(Inv(1), 21, 2), Items[21], ctx));

        // Through the planner: the spare shows up ticked, the unregistered roll stays a guarded hand-pick.
        var plan = new RunPlanner().Build(
            [ScannedItem.Simple(Inv(0), 20, 1), ScannedItem.Simple(Inv(1), 21, 2)],
            new PlannerInputs
            {
                Context = ctx, Profile = MakeProfile(), InfoLookup = Lookup, ProtectList = new ItemList(), AlwaysDiscardList = new ItemList(),
                SessionSkips = new HashSet<string>(), IsAvailable = (k, _) => true, RetainerNames = new Dictionary<ulong, string>(), IncludeUnproposed = true,
            });
        var spareRow = plan.AllRows.Single(r => r.Item.ItemId == 20);
        Assert.True(spareRow.Checked);
        Assert.Equal(RegisteredDuplicateRule.RuleId, spareRow.Proposal.RuleId);
        var rollRow = plan.AllRows.Single(r => r.Item.ItemId == 21);
        Assert.False(rollRow.Checked);
        Assert.Equal("manual", rollRow.Proposal.RuleId);
    }

    [Fact]
    public void Presets_are_distinct_and_detectable()
    {
        Assert.Equal(PresetName.MarketBoard, Presets.Detect(Presets.For(PresetName.MarketBoard)));
        Assert.Equal(PresetName.Vendor, Presets.Detect(Presets.For(PresetName.Vendor)));
        Assert.Equal(PresetName.DiscardAll, Presets.Detect(Presets.For(PresetName.DiscardAll)));
        var custom = Presets.For(PresetName.Vendor);
        custom.SoftCapItems = 51;
        Assert.Equal(PresetName.Custom, Presets.Detect(custom));
    }
}
