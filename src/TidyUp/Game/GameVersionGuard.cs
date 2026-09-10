using TidyUp.Core.Integrations;

namespace TidyUp.Game;

/// <summary>
/// Which game version this build of Gleam was checked against, and whether the running game is it.
/// See <see cref="GameVersionGate"/> for what is held back on any other version.
/// </summary>
public static class GameVersionGuard
{
    /// <summary>
    /// The game version this build was last checked on in game. Move it forward after checking a new patch;
    /// until then players on that patch are asked before anything runs hands-free.
    /// </summary>
    public const string CheckedAgainst = "2026.09.01.0000.0000";

    public const string HeldReason = "the game has been patched since this version of Gleam was checked";

    private static string? current;
    private static bool read;

    /// <summary>The running game's version, read once. Null when neither the game folder nor memory says.</summary>
    public static string? Current()
    {
        if (read) return current;
        current = ReadVersionFile() ?? ReadFromMemory();
        read = true;
        return current;
    }

    public static GameVersionStatus Status(Configuration config) => GameVersionGate.Check(Current(), CheckedAgainst, config.AcceptedGameVersion);

    public static bool AllowsUnattended(Configuration config) => GameVersionGate.AllowsUnattended(Status(config));

    /// <summary>The player's go-ahead for this patch. The next patch is held back again.</summary>
    public static void AcceptCurrent(Configuration config) => config.AcceptedGameVersion = Current();

    /// <summary>The launcher keeps the version beside the game itself, in the same form patch notes use.</summary>
    private static string? ReadVersionFile()
    {
        try
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath);
            if (dir is null) return null;
            var file = Path.Combine(dir, "ffxivgame.ver");
            if (!File.Exists(file)) return null;
            var text = File.ReadAllText(file).Trim();
            return text.Length == 0 ? null : text;
        }
        catch
        {
            return null;
        }
    }

    private static unsafe string? ReadFromMemory()
    {
        try
        {
            var fw = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            if (fw == null) return null;
            var text = fw->GameVersionString;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch
        {
            return null;
        }
    }
}
