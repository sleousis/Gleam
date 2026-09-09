using TidyUp.Core.Integrations;

namespace TidyUp.Core.Tests;

public class DiscardHelperImportTests
{
    /// <summary>The shape of a real ARDiscard.json, taken from a live install.</summary>
    private const string RealFile = """
        {
          "$type": "ARDiscard.Configuration, ARDiscard",
          "Version": 3,
          "RunAfterVenture": false,
          "RunBeforeLogout": false,
          "DiscardingItems": [5, 12, 4551],
          "BlacklistedItems": [2820],
          "ExcludedCharacters": [],
          "Armoury": { "$type": "ARDiscard.Configuration+ArmouryConfiguration, ARDiscard", "MaximumGearItemLevel": 45 },
          "IgnoreItemCountWhenAbove": 50
        }
        """;

    [Fact]
    public void The_two_lists_never_change_places()
    {
        var lists = DiscardHelperImport.Parse(RealFile);

        // The whole point: what it throws away and what it protects must not be mixed up.
        Assert.Equal([5u, 12u, 4551u], lists.Discard);
        Assert.Equal([2820u], lists.Keep);
        Assert.DoesNotContain(2820u, lists.Discard);
        Assert.False(lists.IsEmpty);
    }

    [Fact]
    public void An_empty_discard_list_brings_nothing_across_as_junk()
    {
        // The common case for someone who only ever built a blacklist: importing must not turn it into junk.
        var lists = DiscardHelperImport.Parse("""{"DiscardingItems": [], "BlacklistedItems": [2820]}""");
        Assert.Empty(lists.Discard);
        Assert.Equal([2820u], lists.Keep);
    }

    [Fact]
    public void Other_numbers_in_the_file_are_left_alone()
    {
        // Version, item-level caps and counts are numbers too, and none of them are item ids.
        var lists = DiscardHelperImport.Parse(RealFile);
        Assert.DoesNotContain(3u, lists.Discard);
        Assert.DoesNotContain(45u, lists.Discard);
        Assert.DoesNotContain(50u, lists.Discard);
    }

    [Fact]
    public void What_is_written_can_be_read_back_unchanged()
    {
        var mine = new DiscardHelperLists([5u, 12u], [2820u]);
        var text = DiscardHelperImport.Write(mine);
        var back = DiscardHelperImport.Parse(text);

        Assert.Equal(mine.Discard, back.Discard);
        Assert.Equal(mine.Keep, back.Keep);
        // Discard Helper recognises its own files by this marker.
        Assert.Contains("\"$type\": \"ARDiscard.Configuration, ARDiscard\"", text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{}")]
    [InlineData("""{"DiscardingItems": "nonsense"}""")]
    [InlineData("""{"DiscardingItems": [0, 99999999, -4, "x", null]}""")]
    public void Anything_unexpected_yields_nothing_rather_than_throwing(string json)
    {
        var lists = DiscardHelperImport.Parse(json);
        Assert.True(lists.IsEmpty);
    }
}
