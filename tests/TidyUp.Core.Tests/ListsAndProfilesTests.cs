using TidyUp.Core.Lists;
using TidyUp.Core.Model;
using TidyUp.Core.Rules;
using static TidyUp.Core.Tests.TestData;

namespace TidyUp.Core.Tests;

public class ListsAndProfilesTests
{
    [Fact]
    public void HardBlocks_catch_indisposable_unique_untradeable_category_gearset_and_plates()
    {
        var ctx = Context(gearsetItems: [5u], plateItems: [6u]);
        Assert.Equal(HardBlockReason.Indisposable, HardBlocks.Check(ScannedItem.Simple(Inv(0), 10, 1), Items[10], ctx));
        Assert.Equal(HardBlockReason.UniqueUntradeable, HardBlocks.Check(ScannedItem.Simple(Inv(0), 9, 1), Items[9], ctx));
        Assert.Equal(HardBlockReason.Currency, HardBlocks.Check(ScannedItem.Simple(Inv(0), 11, 1), Items[11], ctx));
        Assert.True(HardBlocks.IsImmovable(HardBlockReason.Currency));
        Assert.Equal(HardBlockReason.InGearset, HardBlocks.Check(ScannedItem.Simple(Arm(0), 5, 1), Items[5], ctx));
        Assert.Equal(HardBlockReason.InGlamourPlate, HardBlocks.Check(ScannedItem.Simple(SlotRef.Dresser(1), 6, 1), Items[6], ctx));

        // A plate reference does not block the *inventory* copy, and a gearset does not block the dresser copy.
        Assert.Equal(HardBlockReason.None, HardBlocks.Check(ScannedItem.Simple(Inv(0), 6, 1), Items[6], ctx));
        Assert.Equal(HardBlockReason.None, HardBlocks.Check(ScannedItem.Simple(SlotRef.Dresser(2), 5, 1), Items[5], ctx));
    }

    [Fact]
    public void HardBlocks_refuse_untradeable_items_with_no_vendor_value_unless_curated_seasonal()
    {
        // Phial of Fantasia: untradeable, 0g, not equipment. Looks like junk to every heuristic; must never be proposable.
        Assert.Equal(HardBlockReason.IrreplaceableUntradeable, HardBlocks.Check(ScannedItem.Simple(Inv(0), 16, 1), Items[16], Context()));
        Assert.Equal(HardBlockReason.IrreplaceableUntradeable, HardBlocks.Check(ScannedItem.Simple(Inv(0), 2, 1), Items[2], Context()));
        // A curated seasonal entry is a deliberate human decision and is allowed through to its rule.
        Assert.Equal(HardBlockReason.None, HardBlocks.Check(ScannedItem.Simple(Inv(0), 8, 1), Items[8], Context(seasonal: [8u])));
        // Untradeable gear with no vendor value is equipment and stays eligible (gearset/plate checks still apply).
        Assert.Equal(HardBlockReason.None, HardBlocks.Check(ScannedItem.Simple(SlotRef.Dresser(0), 5, 1), Items[5], Context()));
    }

    [Fact]
    public void ItemList_matches_by_character_scope_and_hq_flag()
    {
        var list = new ItemList();
        list.Add(1, characterId: null);
        list.Add(2, characterId: 0xAAAA, includeHq: false);

        Assert.True(list.Contains(1, false, 0xBBBB));
        Assert.True(list.Contains(1, true, 0xBBBB));
        Assert.True(list.Contains(2, false, 0xAAAA));
        Assert.False(list.Contains(2, true, 0xAAAA));   // HQ excluded
        Assert.False(list.Contains(2, false, 0xBBBB));  // other character
    }

    [Fact]
    public void ItemList_add_is_idempotent_and_remove_is_scoped()
    {
        var list = new ItemList();
        Assert.True(list.Add(7));
        Assert.False(list.Add(7, note: "again"));
        Assert.Single(list.Entries);
        Assert.Equal("again", list.Entries[0].Note);

        list.Add(7, characterId: 1);
        Assert.Equal(2, list.Entries.Count);
        Assert.Equal(1, list.Remove(7, characterId: 1));
        Assert.Single(list.Entries);
        Assert.Equal(1, list.RemoveAll(7));
        Assert.Empty(list.Entries);
    }

    [Fact]
    public void ProfileStore_effective_profile_applies_only_overridden_properties()
    {
        var store = new ProfileStore();
        store.Account.ApplyPreset(PresetName.DiscardAll);
        store.Account.PostDutyNudge = true;

        var o = store.GetOrCreateOverride(0xC0FFEE, "Alt");
        o.Values.Thresholds.SoftCapItems = 999;
        o.Values.PostDutyNudge = false;

        // Nothing overridden yet: account wins.
        var eff = store.Effective(0xC0FFEE);
        Assert.Equal(Presets.For(PresetName.DiscardAll).SoftCapItems, eff.Thresholds.SoftCapItems);
        Assert.True(eff.PostDutyNudge);

        store.SetOverridden(0xC0FFEE, "Alt", nameof(Profile.PostDutyNudge), true);
        o.Values.PostDutyNudge = false;
        eff = store.Effective(0xC0FFEE);
        Assert.False(eff.PostDutyNudge);
        Assert.Equal(Presets.For(PresetName.DiscardAll).SoftCapItems, eff.Thresholds.SoftCapItems);

        store.SetOverridden(0xC0FFEE, "Alt", nameof(Profile.Thresholds), true);
        o.Values.Thresholds.SoftCapItems = 999;
        eff = store.Effective(0xC0FFEE);
        Assert.Equal(999, eff.Thresholds.SoftCapItems);

        // Other characters are untouched.
        Assert.Equal(Presets.For(PresetName.DiscardAll).SoftCapItems, store.Effective(0xDEAD).Thresholds.SoftCapItems);
    }

    [Fact]
    public void ProfileStore_override_never_aliases_account_state()
    {
        var store = new ProfileStore();
        store.SetOverridden(1, "A", nameof(Profile.EnabledRules), true);
        var o = store.Overrides.Single();
        o.Values.EnabledRules.Remove(VendorOnlyJunkRule.RuleId);
        Assert.Contains(VendorOnlyJunkRule.RuleId, store.Account.EnabledRules);
        Assert.DoesNotContain(VendorOnlyJunkRule.RuleId, store.Effective(1).EnabledRules);
    }

    [Fact]
    public void Profile_clone_is_deep()
    {
        var p = new Profile();
        var c = p.Clone();
        c.Thresholds.SoftCapGil = 1;
        c.EnabledRules.Clear();
        c.ContainerEnabled[ContainerKind.Retainer] = false;
        Assert.NotEqual(1, p.Thresholds.SoftCapGil);
        Assert.NotEmpty(p.EnabledRules);
        Assert.True(p.IsContainerEnabled(ContainerKind.Retainer));
    }
}
