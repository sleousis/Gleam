using System.Text.RegularExpressions;

namespace Gleam.Core.Integrations;

/// <summary>
/// The names Gleam went by inside before 0.10, when its assembly, namespaces and files were called TidyUp.
/// Dalamud files a plugin's settings and history under its internal name, and shared layouts carry a
/// prefix, so this is the one place the old name stays: to bring a player's things across on the first
/// start under the new name, and to read layouts shared before the rename.
/// </summary>
public static class LegacyNames
{
    /// <summary>The internal name Dalamud knew the plugin by, which is also its old settings file and folder.</summary>
    public const string InternalName = "TidyUp";

    /// <summary>What a layout exported before the rename starts with.</summary>
    public const string LayoutPrefix = "TIDYUP1:";

    /// <summary>History files in the old settings folder, and the names they have now.</summary>
    public static readonly IReadOnlyList<(string From, string To)> HistoryFiles =
    [
        ("tidyup-history.jsonl", "gleam-history.jsonl"),
        ("tidyup-moves.jsonl", "gleam-moves.jsonl"),
    ];

    private static readonly Regex TypeMarker = new("""("\$type"\s*:\s*")([^"]*)(")""", RegexOptions.Compiled);
    private static readonly Regex OldName = new(@"\bTidyUp\b", RegexOptions.Compiled);

    /// <summary>
    /// Settings saved before the rename, made readable now. Dalamud stores each object with its type's full
    /// name ("TidyUp.Configuration, TidyUp"), and that type no longer exists. Only those markers change: a
    /// layout or list the player happened to call "TidyUp" keeps its name.
    /// </summary>
    public static string RewriteConfig(string json) =>
        TypeMarker.Replace(json, m => m.Groups[1].Value + OldName.Replace(m.Groups[2].Value, "Gleam") + m.Groups[3].Value);
}
