using Gleam.Core.Execution;

namespace Gleam.Core.Tests;

/// <summary>Recognising the game's menus and questions from the rows they are built from, in any client language.</summary>
public class LanguageFixTests
{
    [Theory]
    // The game fills the slot count in: the row has none.
    [InlineData("Entrust or withdraw items. (Slots filled: )", "Entrust or withdraw items. (Slots filled: 12/175)", true)]
    [InlineData("アイテムの受け渡し（所持品数：/）", "アイテムの受け渡し（所持品数：12/175）", true)]
    // Long questions are broken across lines in the window.
    [InlineData("Renvoyer le servant effacera la liste de rachat. Confirmer ?", "Renvoyer le servant effacera la\r\nliste de rachat. Confirmer ?", true)]
    [InlineData("Undertake supply and provisioning missions.", "Undertake supply and provisioning missions.", true)]
    // A different entry is not the one.
    [InlineData("Sell items in your inventory on the market.", "Sell items in your retainer's inventory on the market.", false)]
    [InlineData("Quit.", "Entrust or withdraw items.", false)]
    public void An_entry_is_recognised_by_exactly_the_words_of_its_row(string row, string entry, bool expected) =>
        Assert.Equal(expected, MenuText.HasPieces(entry, MenuText.Pieces(row)));

    [Fact]
    public void Numbers_and_punctuation_are_not_part_of_a_row()
    {
        Assert.Equal(["slots", "filled"], MenuText.Pieces("(Slots filled: 12/175)"));
    }

    [Fact]
    public void A_german_adjective_matches_whatever_ending_the_sentence_gives_it()
    {
        var name = $"fünfblättrig{PromptMatch.InflectedEnding} Ahornzweige";

        Assert.True(PromptMatch.Mentions("Möchtest du 3 fünfblättrige Ahornzweige wegwerfen?", name));
        Assert.True(PromptMatch.Mentions("Willst du die fünfblättrigen Ahornzweige wegwerfen?", name));
        Assert.False(PromptMatch.Mentions("Möchtest du 3 vierblättrige Ahornzweige wegwerfen?", name));
    }
}
