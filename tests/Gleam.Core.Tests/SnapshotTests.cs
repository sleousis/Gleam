using Gleam.Core.Lists;
using Gleam.Core.Model;
using Gleam.Core.Organizer.Model;

namespace Gleam.Core.Tests;

/// <summary>
/// Copies the background work plans from. The settings page and the organizer page edit the originals in place
/// while a scan or a preview runs on the thread pool, so a copy must share nothing with them.
/// </summary>
public class SnapshotTests
{
    [Fact]
    public void A_layout_snapshot_keeps_its_ids_and_shares_nothing()
    {
        var plan = OrganizerPlan.Starter();
        plan.Simple = true;
        plan.RetainersInScope.Add(7);

        var copy = plan.Snapshot();

        Assert.Equal(plan.Id, copy.Id);
        Assert.True(copy.Simple);
        Assert.Equal(plan.Rules.Select(r => r.Id), copy.Rules.Select(r => r.Id));

        // Editing the original, the way the page does, leaves the copy as it was.
        plan.Rules.RemoveAt(0);
        plan.Rules[0].When.Tags!.Add(ItemTag.Materia);
        plan.Rules[0].Then.Kind = DestinationKind.Saddlebag;
        plan.Fallback.Kind = DestinationKind.Bags;
        plan.RetainersInScope.Add(8);

        Assert.Equal(5, copy.Rules.Count);
        Assert.DoesNotContain(ItemTag.Materia, copy.Rules[1].When.Tags!);
        Assert.Equal(DestinationKind.Bags, copy.Rules[1].Then.Kind);
        Assert.Equal(DestinationKind.Stay, copy.Fallback.Kind);
        Assert.Equal(new[] { 7ul }, copy.RetainersInScope);
    }

    [Fact]
    public void A_clone_is_a_new_layout_and_no_longer_shares_destinations()
    {
        var plan = OrganizerPlan.Starter();
        var clone = plan.Clone();

        Assert.NotEqual(plan.Id, clone.Id);
        plan.Rules[0].Then.Kind = DestinationKind.Bags;
        Assert.Equal(DestinationKind.Saddlebag, clone.Rules[0].Then.Kind);
    }

    [Fact]
    public void A_list_snapshot_shares_nothing_with_the_list()
    {
        var list = new ItemList();
        list.Add(5, characterId: 9, note: "keep", includeHq: false);

        var copy = list.Snapshot();
        list.Add(6);
        list.Entries[0].IncludeHq = true;

        Assert.Single(copy.Entries);
        Assert.False(copy.Entries[0].IncludeHq);
        Assert.True(copy.Contains(5, false, 9));
        Assert.False(copy.Contains(5, true, 9));
        Assert.False(copy.Contains(6, false, 9));
    }
}
