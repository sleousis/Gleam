using System.Text.RegularExpressions;

namespace Gleam.Core.Execution;

/// <summary>
/// Recognises a menu entry or a prompt by the game text it is built from. The game fills numbers into some of them
/// ("Slots filled: 12/175") and breaks long ones across lines, so the text's words are compared in order and its
/// numbers and punctuation are left out. This works the same in every client language.
/// </summary>
public static class MenuText
{
    private static readonly Regex NotAWord = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    /// <summary>A text's words, lower-cased, in order; runs of digits alone are dropped.</summary>
    public static string[] Pieces(string text) =>
        NotAWord.Split(text.ToLowerInvariant()).Where(p => p.Length > 0 && !p.All(char.IsDigit)).ToArray();

    /// <summary>
    /// Whether the entry has exactly these words, in this order, once numbers, punctuation and the game's formatting
    /// bytes are left out. Words merely appearing in order was too loose: the row for selling from your bags then also
    /// matched the entry for selling from the retainer's inventory.
    /// </summary>
    public static bool HasPieces(string entry, IReadOnlyList<string> pieces)
    {
        if (pieces.Count == 0 || string.IsNullOrEmpty(entry)) return false;
        var words = Pieces(StripPayloads(entry));
        return words.Length == pieces.Count && words.SequenceEqual(pieces);
    }

    /// <summary>The game's text carries formatting between 0x02 and 0x03; its bytes are not words.</summary>
    private static string StripPayloads(string s)
    {
        if (s.IndexOf('\u0002') < 0) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        var inside = false;
        foreach (var ch in s)
        {
            if (ch == '\u0002') { inside = true; continue; }
            if (ch == '\u0003') { inside = false; sb.Append(' '); continue; }
            if (!inside) sb.Append(ch);
        }
        return sb.ToString();
    }
}
