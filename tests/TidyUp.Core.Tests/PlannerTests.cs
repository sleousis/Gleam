using TidyUp.Core.Lists;
using TidyUp.Core.Model;
using TidyUp.Core.Planning;
using TidyUp.Core.Rules;
using static TidyUp.Core.Tests.TestData;

namespace TidyUp.Core.Tests;

public class PlannerTests
{
    private static PlannerInputs Inputs(
        ItemContext? ctx = null,
        Profile? profile = null,
        ItemList? protect = null,
        ItemList? always = null,
        IReadOnlySet<string>? skips = null,
        Func<ContainerKind, ulong, bool>? available = null) => new()
    {
        Context = ctx ?? Context(),
        Profile = profile ?? MakeProfile(),
        InfoLookup = Lookup,
        ProtectList = protect ?? new ItemList(),
        AlwaysDiscardList = always ?? new ItemList(),
        SessionSkips = skips ?? new HashSet<string>(),
        IsAvailable = available ?? ((k, _) => k.IsAlwaysLoaded()),
        RetainerNames = new Dictionary<ulong, string> { [0xBEEF] = "Retainer B" },
    };

    [Fact]
    public void Protect_list_is_applied_before_rules_and_hard_blocks_beat_the_blacklist()
    {
        var protect = new ItemList(); protect.Add(1);
        var always = new ItemList(); always.Add(10); always.Add(9); // both hard-blocked

        var items = new[]
        {
            ScannedItem.Simple(Inv(0), 1, 14),
            ScannedItem.Simple(Inv(1), 10, 1),
            ScannedItem.Simple(Inv(2), 9, 1),
        };
        var plan = new RunPlanner().Build(items, Inputs(protect: protect, always: always));

        Assert.Empty(plan.AllRows);
        Assert.Equal(3, plan.Excluded.Count);
        Assert.Contains(plan.Excluded, e => e.Item.ItemId == 1 && !e.IsHardBlock);
        Assert.Contains(plan.Excluded, e => e.Item.ItemId == 10 && e.IsHardBlock);
        Assert.Contains(plan.Excluded, e => e.Item.ItemId == 9 && e.IsHardBlock);
    }

    [Fact]
    public void Blacklist_creates_user_rows_that_start_checked()
    {
        var always = new ItemList(); always.Add(15);
        var plan = new RunPlanner().Build([ScannedItem.Simple(Inv(0), 15, 3)], Inputs(always: always));
        var row = Assert.Single(plan.AllRows);
        Assert.Equal(Confidence.User, row.Proposal.Confidence);
        Assert.True(row.Checked);
        Assert.Equal(ActionKind.VendorSell, row.ChosenAction);
        Assert.Equal(15, row.Proposal.ValueGil);
    }

    [Fact]
    public void Sections_group_by_container_and_owner_with_availability_and_requirement()
    {
        var items = new[]
        {
            ScannedItem.Simple(Inv(0), 1, 1),
            ScannedItem.Simple(Arm(0), 4, 1),
            ScannedItem.Simple(Saddle(0), 1, 1),
            ScannedItem.Simple(Ret(0), 1, 1, owner: "Retainer B"),
            ScannedItem.Simple(Ret(0, 0xCAFE), 1, 1, owner: "Retainer C"),
            ScannedItem.Simple(SlotRef.Dresser(3), 6, 1),
        };
        var plan = new RunPlanner().Build(items, Inputs());

        Assert.Equal(6, plan.Sections.Count);
        var order = plan.Sections.Select(s => s.Kind).ToList();
        Assert.Equal(ContainerKind.Inventory, order[0]);
        Assert.Equal(ContainerKind.GlamourDresser, order[^1]);

        var saddle = plan.Sections.Single(s => s.Kind == ContainerKind.Saddlebag);
        Assert.False(saddle.IsAvailableNow);
        Assert.Contains("saddlebag", saddle.Requirement);

        var retB = plan.Sections.Single(s => s.Kind == ContainerKind.Retainer && s.OwnerId == 0xBEEF);
        Assert.Equal("Retainer: Retainer B", retB.Title);
        var retC = plan.Sections.Single(s => s.Kind == ContainerKind.Retainer && s.OwnerId == 0xCAFE);
        Assert.Equal("Retainer: Retainer C", retC.Title); // falls back to the scanned owner name
    }

    [Fact]
    public void Session_skips_uncheck_matching_rows_only()
    {
        var items = new[] { ScannedItem.Simple(Inv(0), 1, 14), ScannedItem.Simple(Inv(1), 1, 3) };
        var first = new RunPlanner().Build(items, Inputs());
        var skipKey = first.AllRows.First(r => r.Item.Quantity == 14).Key;

        var second = new RunPlanner().Build(items, Inputs(skips: new HashSet<string> { skipKey }));
        Assert.False(second.AllRows.Single(r => r.Item.Quantity == 14).Checked);
        Assert.True(second.AllRows.Single(r => r.Item.Quantity == 3).Checked);
    }

    [Fact]
    public void Disabled_containers_and_excluded_retainers_are_never_scanned_into_the_plan()
    {
        var profile = MakeProfile();
        profile.ContainerEnabled[ContainerKind.Saddlebag] = false;
        profile.ExcludedRetainerIds.Add(0xBEEF);
        var items = new[]
        {
            ScannedItem.Simple(Saddle(0), 1, 1),
            ScannedItem.Simple(Ret(0), 1, 1),
            ScannedItem.Simple(Ret(0, 0xCAFE), 1, 1),
        };
        var plan = new RunPlanner().Build(items, Inputs(profile: profile));
        var row = Assert.Single(plan.AllRows);
        Assert.Equal(0xCAFEu, row.Item.Slot.OwnerId);
    }

    [Fact]
    public void Rule_action_override_swaps_primary_when_it_is_an_allowed_alternative()
    {
        var profile = MakeProfile();
        profile.RuleActionOverrides[ObsoleteGearRule.RuleId] = ActionKind.Desynth;
        var plan = new RunPlanner().Build([ScannedItem.Simple(Arm(0), 4, 1)], Inputs(profile: profile));
        var row = Assert.Single(plan.AllRows);
        Assert.Equal(ActionKind.Desynth, row.ChosenAction);
        Assert.Contains(ActionKind.ExpertDelivery, row.Proposal.Alternatives);
    }

    [Fact]
    public void Summary_reports_slots_freed_gil_destroyed_and_recovered()
    {
        var always = new ItemList(); always.Add(15); // 5g vendor, marketable → user row, sell
        var items = new[] { ScannedItem.Simple(Inv(0), 1, 10), ScannedItem.Simple(Inv(1), 15, 1), ScannedItem.Simple(Arm(0), 4, 1) };
        var plan = new RunPlanner().Build(items, Inputs(always: always));
        var s = plan.Summarize();

        Assert.Equal(3, s.TotalRows);
        Assert.Equal(3, s.CheckedRows);
        Assert.Equal(2, s.SlotsFreedByContainer[ContainerKind.Inventory]);
        Assert.Equal(1, s.SlotsFreedByContainer[ContainerKind.Armoury]);
        // Balanced sells the tradeable doublet (100g) instead of turning it in; seals stay an alternative.
        Assert.Equal(385, s.GilRecovered);
        Assert.Equal(0, s.GilDestroyed);
        Assert.Equal(0, s.SealsRows);
    }

    [Fact]
    public void Presets_mean_exactly_what_they_say()
    {
        // item 1: tradeable, vendor 28 · item 2: untradeable, 0g (hard-blocked) · item 3: untradeable medicine, vendor 45 · item 4: tradeable gear, vendor 100
        var items = new[] { ScannedItem.Simple(Inv(0), 1, 5), ScannedItem.Simple(Inv(1), 3, 9), ScannedItem.Simple(Arm(0), 4, 1) };

        var cautious = new RunPlanner().Build(items, Inputs(profile: MakeProfile(PresetName.Cautious)));
        Assert.All(cautious.AllRows, r => Assert.Equal(ActionKind.VendorSell, r.ChosenAction));
        Assert.DoesNotContain(cautious.AllRows, r => r.Info.IsUntradable);
        Assert.Contains(cautious.Excluded, e => e.Item.ItemId == 3 && e.Reason.Contains("Cautious"));

        var balanced = new RunPlanner().Build(items, Inputs(profile: MakeProfile(PresetName.Balanced)));
        Assert.Equal(ActionKind.VendorSell, balanced.AllRows.Single(r => r.Item.ItemId == 1).ChosenAction);
        Assert.Equal(ActionKind.Discard, balanced.AllRows.Single(r => r.Item.ItemId == 3).ChosenAction);
        Assert.Equal(ActionKind.VendorSell, balanced.AllRows.Single(r => r.Item.ItemId == 4).ChosenAction);
        Assert.Contains(ActionKind.ExpertDelivery, balanced.AllRows.Single(r => r.Item.ItemId == 4).Proposal.Alternatives);

        var aggressive = new RunPlanner().Build(items, Inputs(profile: MakeProfile(PresetName.Aggressive)));
        Assert.Equal(3, aggressive.AllRows.Count());
        Assert.All(aggressive.AllRows, r => Assert.Equal(ActionKind.Discard, r.ChosenAction));
    }

    [Fact]
    public void Items_outside_the_players_bags_can_only_be_discarded()
    {
        // Same vendor-priced junk: sell in the inventory, discard-only in a retainer or the saddlebag.
        var items = new[] { ScannedItem.Simple(Inv(0), 1, 5), ScannedItem.Simple(Ret(0), 1, 5), ScannedItem.Simple(Saddle(0), 1, 5) };
        var plan = new RunPlanner().Build(items, Inputs());
        var rows = plan.AllRows.ToDictionary(r => r.Item.Slot.Kind);

        Assert.Equal(ActionKind.VendorSell, rows[ContainerKind.Inventory].ChosenAction);
        Assert.Equal(ActionKind.Discard, rows[ContainerKind.Retainer].ChosenAction);
        Assert.Empty(rows[ContainerKind.Retainer].Proposal.Alternatives);
        Assert.Contains("withdraw", rows[ContainerKind.Retainer].Proposal.Reason);
        Assert.Equal(ActionKind.Discard, rows[ContainerKind.Saddlebag].ChosenAction);

        // The always-discard list follows the same physics.
        var always = new ItemList(); always.Add(15);
        var forced = new RunPlanner().Build([ScannedItem.Simple(Ret(0), 15, 1)], Inputs(always: always));
        Assert.Equal(ActionKind.Discard, Assert.Single(forced.AllRows).ChosenAction);
    }

    [Fact]
    public void A_fantasia_on_the_always_discard_list_is_still_never_proposed()
    {
        var always = new ItemList(); always.Add(16);
        var plan = new RunPlanner().Build([ScannedItem.Simple(Inv(0), 16, 1)], Inputs(always: always));
        Assert.Empty(plan.AllRows);
        var ex = Assert.Single(plan.Excluded);
        Assert.True(ex.IsHardBlock);
        Assert.Contains("cannot be bought back", ex.Reason);
    }

    [Fact]
    public void SoftCap_trips_on_items_or_gil_whichever_first()
    {
        var t = new Thresholds { SoftCapItems = 2, SoftCapGil = 1_000_000 };
        var rows = Enumerable.Range(0, 3).Select(i => new PlanRow
        {
            Proposal = new Proposal { Item = ScannedItem.Simple(Inv(i), 1, 1), Info = Items[1], Action = ActionKind.VendorSell, Confidence = Confidence.High, RuleId = "r", Reason = "r" },
            Checked = true, ChosenAction = ActionKind.VendorSell,
        }).ToList();

        var r = SoftCap.Evaluate(rows, t);
        Assert.True(r.Exceeded);
        Assert.Equal(3, r.Items);
        Assert.Equal("Accept 3 (exceeds cap)", r.ButtonLabel("Accept"));

        var gilCap = new Thresholds { SoftCapItems = 100, SoftCapGil = 50 };
        Assert.True(SoftCap.Evaluate(rows, gilCap).Exceeded); // 3 × 28g = 84g > 50

        rows[2].Checked = false;
        var fine = new Thresholds { SoftCapItems = 2, SoftCapGil = 1_000_000 };
        Assert.False(SoftCap.Evaluate(rows, fine).Exceeded);
    }
}
