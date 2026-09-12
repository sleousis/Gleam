using Gleam.Core.Model;

namespace Gleam.Core.Tests;

/// <summary>Knowing a character's retainers before the game lists them, which it does only after a summoning bell.</summary>
public class RetainerRosterTests
{
    private static readonly Dictionary<ulong, string> NoneYet = new();
    private static readonly Dictionary<ulong, string> Saved = new() { [1] = "Kima'hri", [2] = "Thordan", [3] = "An alt's retainer" };
    private static readonly Dictionary<ulong, ulong> Owners = new() { [1] = 100, [2] = 100, [3] = 200 };

    [Fact]
    public void Before_a_bell_the_saved_retainers_of_this_character_stand_in() =>
        Assert.Equal([1UL, 2UL], RetainerRoster.Mine(NoneYet, Saved, Owners, 100).Keys.Order());

    [Fact]
    public void Once_the_game_lists_them_its_list_wins()
    {
        var live = new Dictionary<ulong, string> { [2] = "Thordan" };
        Assert.Equal([2UL], RetainerRoster.Mine(live, Saved, Owners, 100).Keys);
    }

    [Fact]
    public void A_retainer_whose_owner_was_never_seen_is_claimed_for_nobody()
    {
        var unseen = new Dictionary<ulong, string> { [9] = "Nobody knows" };
        Assert.Empty(RetainerRoster.Mine(NoneYet, unseen, Owners, 100));
        Assert.False(RetainerRoster.BelongsToSomeoneElse(9, Owners, 100));
    }

    [Fact]
    public void Another_characters_retainer_is_said_to_be_theirs()
    {
        Assert.True(RetainerRoster.BelongsToSomeoneElse(3, Owners, 100));
        Assert.False(RetainerRoster.BelongsToSomeoneElse(1, Owners, 100));
    }

    [Fact]
    public void Logged_out_nothing_is_claimed()
    {
        Assert.Empty(RetainerRoster.Mine(NoneYet, Saved, Owners, 0));
        Assert.False(RetainerRoster.BelongsToSomeoneElse(3, Owners, 0));
    }
}
