namespace Gleam.Core.Integrations;

public enum GameVersionStatus
{
    /// <summary>The game is the version this build of Gleam was checked against.</summary>
    Checked,
    /// <summary>A newer game, which the player chose to run anyway.</summary>
    AcceptedByPlayer,
    /// <summary>A game version nobody has checked Gleam against yet.</summary>
    Unchecked,
    /// <summary>The version could not be read. Nothing is held back on a guess.</summary>
    Unknown,
}

/// <summary>
/// Whether Gleam may drive the game while the player is not watching every step.
///
/// A patch can move a menu entry or renumber a dialog's buttons without anything failing loudly: the click
/// still lands, just on the wrong thing. Runs the player watches row by row stay available; hands-free runs
/// and the cleaning after ventures wait until this build has been checked on the patch, or until the player
/// says to go ahead on it.
/// </summary>
public static class GameVersionGate
{
    public static GameVersionStatus Check(string? current, string checkedAgainst, string? acceptedByPlayer)
    {
        if (string.IsNullOrWhiteSpace(current)) return GameVersionStatus.Unknown;
        var now = current.Trim();
        if (string.Equals(now, checkedAgainst.Trim(), StringComparison.Ordinal)) return GameVersionStatus.Checked;
        if (!string.IsNullOrWhiteSpace(acceptedByPlayer) && string.Equals(now, acceptedByPlayer.Trim(), StringComparison.Ordinal))
            return GameVersionStatus.AcceptedByPlayer;
        return GameVersionStatus.Unchecked;
    }

    public static bool AllowsUnattended(GameVersionStatus status) => status != GameVersionStatus.Unchecked;
}
