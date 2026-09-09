using System.Text.Json;
using TidyUp.Core.Model;
using TidyUp.Core.Organizer.Model;
using static TidyUp.Core.Tests.TestData;

namespace TidyUp.Core.Tests;

public class OrganizerModelTests
{
    private static bool NeverTouch(uint id, bool hq) => id == 6;

    [Fact]
    public void Empty_predicate_matches_everything_and_reads_as_everything_else()
    {
        var p = new OrganizerPredicate();
        Assert.True(p.IsEmpty);
        Assert.Equal("everything else", p.Describe());
        Assert.True(p.Matches(ScannedItem.Simple(Inv(0), 1, 5), Items[1], Context(), NeverTouch));
        Assert.True(p.Matches(ScannedItem.Simple(Arm(0), 6, 1), Items[6], Context(), NeverTouch));
    }

    [Fact]
    public void Predicates_and_together()
    {
        // item 6: Woolen Coat, equipment, iL34, Lv34, CjcAll. item 12: Expensive Potion (Medicine).
        var gearUnder40 = new OrganizerPredicate { Tags = [ItemTag.Gear], MaxItemLevel = 40 };
        Assert.True(gearUnder40.Matches(ScannedItem.Simple(Arm(0), 6, 1), Items[6], Context(), NeverTouch));
        Assert.False(gearUnder40.Matches(ScannedItem.Simple(Inv(0), 12, 1), Items[12], Context(), NeverTouch));

        var hqOnly = new OrganizerPredicate { IsHq = true };
        Assert.True(hqOnly.Matches(ScannedItem.Simple(Inv(0), 12, 1, hq: true), Items[12], Context(), NeverTouch));
        Assert.False(hqOnly.Matches(ScannedItem.Simple(Inv(0), 12, 1), Items[12], Context(), NeverTouch));

        // Level predicates never match non-equipment, even with no upper bound.
        Assert.False(new OrganizerPredicate { MinItemLevel = 1 }.Matches(ScannedItem.Simple(Inv(0), 12, 1), Items[12], Context(), NeverTouch));

        // Jobs played: CjcNeverPlayed gear is for jobs at level 1.
        Assert.True(new OrganizerPredicate { ForJobsPlayed = false }.Matches(ScannedItem.Simple(Arm(0), 13, 1), Items[13], Context(), NeverTouch));
        Assert.False(new OrganizerPredicate { ForJobsPlayed = true }.Matches(ScannedItem.Simple(Arm(0), 13, 1), Items[13], Context(), NeverTouch));

        Assert.True(new OrganizerPredicate { OnNeverTouchList = true }.Matches(ScannedItem.Simple(Arm(0), 6, 1), Items[6], Context(), NeverTouch));

        // Gear set membership comes from the character context; non-gear never matches either way.
        var withSet = Context(gearsetItems: [5u]);
        Assert.True(new OrganizerPredicate { InGearset = true }.Matches(ScannedItem.Simple(Arm(0), 5, 1), Items[5], withSet, NeverTouch));
        Assert.False(new OrganizerPredicate { InGearset = true }.Matches(ScannedItem.Simple(Arm(0), 6, 1), Items[6], withSet, NeverTouch));
        Assert.True(new OrganizerPredicate { InGearset = false }.Matches(ScannedItem.Simple(Arm(0), 6, 1), Items[6], withSet, NeverTouch));
        Assert.False(new OrganizerPredicate { InGearset = false }.Matches(ScannedItem.Simple(Inv(0), 12, 1), Items[12], withSet, NeverTouch));
        Assert.Equal("Gear · not in a gear set", new OrganizerPredicate { Tags = [ItemTag.Gear], InGearset = false }.Describe());
        Assert.Equal("Gear · iL ≤ 40", gearUnder40.Describe());
    }

    [Fact]
    public void Destinations_resolve_to_storages_except_stay_and_any_retainer()
    {
        Assert.Null(Destination.Stay.Storage);
        Assert.Null(Destination.AnyRetainer.Storage);
        Assert.True(Destination.AnyRetainer.IsAnyRetainer);
        Assert.Equal(ContainerKind.Saddlebag, Destination.Saddlebag.Storage!.Value.Kind);
        Assert.Equal(0xBEEFul, Destination.RetainerNamed(0xBEEF).Storage!.Value.OwnerId);
        Assert.Equal(Destination.RetainerNamed(1), Destination.RetainerNamed(1));
    }

    [Fact]
    public void Layouts_export_to_text_and_import_with_fresh_ids_and_local_retainers()
    {
        var plan = OrganizerPlan.Starter();
        plan.Rules[0].Then = Destination.RetainerNamed(0xBEEF);
        plan.Rules[1].KeepInBags = 20;
        plan.RetainersInScope.Add(0xBEEF);
        plan.RetainersInScope.Add(0xCAFE);

        var text = OrganizerPlanCodec.Export(plan);
        Assert.StartsWith("TIDYUP1:", text);

        var back = OrganizerPlanCodec.TryImport(text, new[] { 0xCAFEul })!;
        Assert.NotNull(back);
        Assert.NotEqual(plan.Id, back.Id);
        Assert.Equal(plan.Name, back.Name);
        Assert.Equal(plan.Rules.Count, back.Rules.Count);
        Assert.True(back.Rules[0].Then.IsAnyRetainer);          // unknown retainer becomes "any"
        Assert.Equal(20, back.Rules[1].KeepInBags);
        Assert.Equal([0xCAFEul], back.RetainersInScope);

        Assert.Null(OrganizerPlanCodec.TryImport("hello", []));
        Assert.Null(OrganizerPlanCodec.TryImport("TIDYUP1:not base64!", []));
        Assert.Null(OrganizerPlanCodec.TryImport(null, []));
    }

    [Fact]
    public void Named_destinations_are_fresh_instances_so_filling_one_in_place_touches_no_other_rule()
    {
        Assert.NotSame(Destination.Stay, Destination.Stay);
        var a = new OrganizerRule();
        var b = new OrganizerRule();
        Assert.NotSame(a.Then, b.Then);

        // What a populate-style serializer does: write into the object the property already holds.
        typeof(Destination).GetProperty(nameof(Destination.Kind))!.SetValue(a.Then, DestinationKind.Saddlebag);
        Assert.Equal(DestinationKind.Saddlebag, a.Then.Kind);
        Assert.Equal(DestinationKind.Stay, b.Then.Kind);
        Assert.Equal(DestinationKind.Stay, new OrganizerPlan().Fallback.Kind);
        Assert.Equal(DestinationKind.Stay, Destination.Stay.Kind);
    }

    [Fact]
    public void Plans_round_trip_through_json_and_clone_deeply()
    {
        var plan = OrganizerPlan.Starter();
        plan.Rules[0].When.MinItemLevel = 10;
        plan.RetainersInScope.Add(0xBEEF);

        var json = JsonSerializer.Serialize(plan);
        var back = JsonSerializer.Deserialize<OrganizerPlan>(json)!;

        Assert.Equal(plan.Id, back.Id);
        Assert.Equal(plan.Rules.Count, back.Rules.Count);
        Assert.Equal(DestinationKind.Saddlebag, back.Rules[0].Then.Kind);
        Assert.Equal(10, back.Rules[0].When.MinItemLevel);
        Assert.Contains(ItemTag.Materia, back.Rules[0].When.Tags!);
        Assert.Equal(DestinationKind.Retainer, back.Rules[4].Then.Kind);
        Assert.True(back.Rules[4].Then.IsAnyRetainer);
        Assert.False(back.Rules[4].When.InGearset);
        Assert.Contains(0xBEEFul, back.RetainersInScope);

        var clone = plan.Clone();
        Assert.NotEqual(plan.Id, clone.Id);
        clone.Rules[0].When.Tags!.Add(ItemTag.Gear);
        Assert.DoesNotContain(ItemTag.Gear, plan.Rules[0].When.Tags!);
    }
}
