using Gleam.Core.Integrations;

namespace Gleam.Core.Tests;

/// <summary>The "what's new" card: what it reads from the changelog, and when it has anything to say.</summary>
public class ReleaseNotesTests
{
    private const string Changelog = """
        # Changelog

        Intro text that belongs to no release.

        ## 0.11.0

        - Not released yet.

        ## 0.10.0

        - Renamed. Install `Gleam` from the list,
          then remove the old copy.
        - Layouts still import.

        ## 0.9.5

        - Self-test.
        """;

    private static readonly Version Running = new(0, 10, 0, 0);

    [Fact]
    public void Sections_and_their_points_are_read_with_run_on_lines_joined()
    {
        var notes = ReleaseNotes.Parse(Changelog);

        Assert.Equal(3, notes.Count);
        var ten = notes.Single(n => n.Version == new Version(0, 10, 0, 0));
        Assert.Equal(["Renamed. Install Gleam from the list, then remove the old copy.", "Layouts still import."], ten.Points);
    }

    [Fact]
    public void A_player_who_updated_sees_what_changed_since_and_nothing_unreleased()
    {
        var shown = ReleaseNotes.Since(ReleaseNotes.Parse(Changelog), "0.9.5", Running);

        Assert.Equal([new Version(0, 10, 0, 0)], shown.Select(n => n.Version));
    }

    [Fact]
    public void Skipped_releases_all_show_newest_first()
    {
        var shown = ReleaseNotes.Since(ReleaseNotes.Parse(Changelog), "0.9.0", Running);

        Assert.Equal([new Version(0, 10, 0, 0), new Version(0, 9, 5, 0)], shown.Select(n => n.Version));
    }

    [Fact]
    public void A_fresh_install_or_a_player_already_up_to_date_sees_nothing()
    {
        var notes = ReleaseNotes.Parse(Changelog);

        Assert.Empty(ReleaseNotes.Since(notes, null, Running));
        Assert.Empty(ReleaseNotes.Since(notes, "0.10.0", Running));
        Assert.Empty(ReleaseNotes.Since(notes, "0.10.0.0", Running));
    }
}
