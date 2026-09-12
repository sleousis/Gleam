using Gleam.Core.Lists;
using Gleam.Core.Model;

namespace Gleam.Core.Tests;

/// <summary>What the settings page promises about where Gleam may look.</summary>
public class SettingsFixTests
{
    [Fact]
    public void Bags_are_always_in_even_for_a_profile_that_once_unticked_them()
    {
        var p = new Profile();
        p.ContainerEnabled[ContainerKind.Inventory] = false;

        Assert.True(p.IsContainerEnabled(ContainerKind.Inventory));
    }

    [Fact]
    public void Every_other_place_can_still_be_left_alone()
    {
        var p = new Profile();
        foreach (var kind in Enum.GetValues<ContainerKind>().Where(k => k != ContainerKind.Inventory))
        {
            p.ContainerEnabled[kind] = false;
            Assert.False(p.IsContainerEnabled(kind));
        }
    }
}
