using Gleam.Core.Lists;
using Gleam.Core.Model;
using Gleam.Core.Planning;
using Gleam.Core.Rules;
using static Gleam.Core.Tests.TestData;

namespace Gleam.Core.Tests;

/// <summary>Settings that promised more than they did, from the September 2026 settings audit.</summary>
public class CorrectnessFixTests
{
    [Fact]
    public void Discard_all_changes_what_happens_to_junk_and_nothing_else()
    {
        var discard = Presets.For(PresetName.DiscardAll);
        var vendor = Presets.For(PresetName.Vendor);

        Assert.Equal(ActionPolicy.DiscardAll, discard.Policy);
        Assert.Equal(vendor.ObsoleteGearLevelGap, discard.ObsoleteGearLevelGap);
        Assert.Equal(vendor.IncludeGearForUnplayedJobs, discard.IncludeGearForUnplayedJobs);
        Assert.Equal(vendor.VendorOnlyMaxUnitPrice, discard.VendorOnlyMaxUnitPrice);
        Assert.Equal(vendor.SoftCapItems, discard.SoftCapItems);
        Assert.Equal(vendor.SoftCapGil, discard.SoftCapGil);
        Assert.Equal(PresetName.DiscardAll, Presets.Detect(discard));
    }

    [Fact]
    public void A_profile_saved_on_the_old_discard_all_values_is_recognised()
    {
        var old = new Thresholds
        {
            Policy = ActionPolicy.DiscardAll, ObsoleteGearLevelGap = 10, IncludeGearForUnplayedJobs = true, ConsumableItemLevelGap = 120,
            CraftingMatMaxRecipeLevel = 70, CraftingMatCrafterLeadLevels = 5, VendorOnlyMaxUnitPrice = 2000, MarketPremiumFactor = 2.0,
            MarketMinStackValueGil = 10_000, SoftCapItems = 100, SoftCapGil = 150_000,
        };

        Assert.True(Presets.IsLegacyDiscardAll(old));
        Assert.False(Presets.IsLegacyDiscardAll(Presets.For(PresetName.DiscardAll)));
        Assert.False(Presets.IsLegacyDiscardAll(Presets.For(PresetName.Vendor)));
    }

    [Fact]
    public void The_always_clean_list_follows_the_preset_and_stays_ticked()
    {
        var always = new ItemList();
        always.Add(15);
        var inputs = new PlannerInputs
        {
            IncludeUnproposed = true,
            Context = Context(),
            Profile = MakeProfile(PresetName.DiscardAll),
            InfoLookup = Lookup,
            ProtectList = new ItemList(),
            AlwaysDiscardList = always,
            SessionSkips = new HashSet<string>(),
            IsAvailable = (_, _) => true,
            RetainerNames = new Dictionary<ulong, string>(),
        };

        var row = Assert.Single(new RunPlanner().Build([ScannedItem.Simple(Inv(0), 15, 3)], inputs).AllRows);

        Assert.Equal(ActionKind.Discard, row.ChosenAction);
        Assert.True(row.Checked);
    }
}
