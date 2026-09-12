using System.Text;

namespace Gleam.Core.Execution;

/// <summary>Whether a game confirmation names the item Gleam meant to act on.</summary>
public static class PromptMatch
{
    /// <summary>Letters and digits only, lower-cased: immune to soft hyphens, payload bytes and punctuation.</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in StripPayloads(s))
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>
    /// Whether a prompt is about the named item. The name's words must appear in the prompt one after another, each
    /// as it is or with a plural ending, and a word never matches inside a longer one. Matching letters only let
    /// "Potion" pass for a prompt about hi-potions, and "Tea" for one about a steak. Names written without spaces
    /// (Japanese) are matched as one run of text, the way the game writes them into the sentence.
    /// </summary>
    public static bool Mentions(string prompt, string itemName)
    {
        if (string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(itemName)) return false;
        if (itemName.Any(c => c >= '\u2E80')) return Normalize(prompt).Contains(Normalize(itemName), StringComparison.Ordinal);
        var words = Words(prompt);
        var name = Words(itemName);
        if (name.Count == 0) return false;
        for (var start = 0; start + name.Count <= words.Count; start++)
        {
            var all = true;
            for (var k = 0; k < name.Count && all; k++) all = SameWord(words[start + k], name[k]);
            if (all) return true;
        }
        return false;
    }

    /// <summary>Marks a word whose ending changes with the sentence, as German adjectives do ("fünfblättrige").</summary>
    public const char InflectedEnding = '\u0001';

    /// <summary>The same word, its English plural, or, for a word marked as inflected, its stem with a short ending.</summary>
    private static bool SameWord(string inPrompt, string inName)
    {
        if (inName.Length > 1 && inName[^1] == InflectedEnding)
        {
            var stem = inName[..^1];
            return inPrompt.StartsWith(stem, StringComparison.Ordinal) && inPrompt.Length <= stem.Length + 3;
        }
        if (inPrompt == inName) return true;
        if (inPrompt == inName + "s" || inPrompt == inName + "es") return true;
        if (inName.EndsWith('y') && inPrompt == inName[..^1] + "ies") return true;
        if (inName.EndsWith('f') && inPrompt == inName[..^1] + "ves") return true;
        if (inName.EndsWith("fe", StringComparison.Ordinal) && inPrompt == inName[..^2] + "ves") return true;
        if (inName.EndsWith("us", StringComparison.Ordinal) && inPrompt == inName[..^2] + "i") return true;
        return false;
    }

    /// <summary>Lower-cased words in order. Hyphens and apostrophes stay inside a word, so "hi-potion" is one word.</summary>
    private static List<string> Words(string s)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        foreach (var raw in StripPayloads(s) + " ")
        {
            var ch = raw == '\u2019' ? '\'' : raw;
            if (char.IsLetterOrDigit(ch) || (sb.Length > 0 && ch is '-' or '\'' or InflectedEnding))
            {
                sb.Append(char.ToLowerInvariant(ch));
                continue;
            }
            // Soft hyphens and other invisible marks sit inside words; they neither split a word nor belong to it.
            if (char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.Format) continue;
            if (sb.Length > 0) list.Add(sb.ToString().TrimEnd('-', '\''));
            sb.Clear();
        }
        list.RemoveAll(w => w.Length == 0);
        return list;
    }

    /// <summary>The game's text carries formatting between 0x02 and 0x03; its bytes are not words.</summary>
    private static string StripPayloads(string s)
    {
        if (s.IndexOf('\u0002') < 0) return s;
        var sb = new StringBuilder(s.Length);
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
