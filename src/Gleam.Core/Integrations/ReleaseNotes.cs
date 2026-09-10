namespace Gleam.Core.Integrations;

/// <summary>One release's section of the changelog: its version and its points, in order.</summary>
public sealed record ReleaseNote(Version Version, IReadOnlyList<string> Points);

/// <summary>
/// Reads CHANGELOG.md, the one place release notes are written, and decides what a player has not seen yet.
/// The same file feeds Dalamud's installer and the GitHub release, so the three never disagree.
/// </summary>
public static class ReleaseNotes
{
    /// <summary>Every "## X.Y.Z" section with its "- " points. A point may run on over several lines; backticks are dropped.</summary>
    public static IReadOnlyList<ReleaseNote> Parse(string markdown)
    {
        var notes = new List<ReleaseNote>();
        Version? version = null;
        var points = new List<string>();

        void Close()
        {
            if (version is not null) notes.Add(new ReleaseNote(version, points.ToList()));
            points.Clear();
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Close();
                version = Normalize(line[3..].Trim());
                continue;
            }
            if (version is null || line.Length == 0) continue;
            var text = line.TrimStart().Replace("`", string.Empty);
            if (line.TrimStart().StartsWith("- ", StringComparison.Ordinal)) points.Add(text[2..].Trim());
            else if (points.Count > 0) points[^1] += " " + text.Trim();
        }
        Close();
        return notes;
    }

    /// <summary>
    /// What to show a player who last saw <paramref name="lastSeen"/> and now runs <paramref name="current"/>:
    /// every release after the one they saw, up to the one they run, newest first. Nothing for a fresh
    /// install (no version seen yet), and nothing written for a release that is not out yet.
    /// </summary>
    public static IReadOnlyList<ReleaseNote> Since(IReadOnlyList<ReleaseNote> all, string? lastSeen, Version current)
    {
        var seen = Normalize(lastSeen);
        if (seen is null) return Array.Empty<ReleaseNote>();
        var now = Normalize(current.ToString())!;
        return all.Where(n => n.Version > seen && n.Version <= now).OrderByDescending(n => n.Version).ToList();
    }

    /// <summary>"0.10.0" and "0.10.0.0" are the same release; <see cref="Version"/> alone would order them apart.</summary>
    public static Version? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !Version.TryParse(text.Trim(), out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }
}
