using TidyUp.Services;

namespace TidyUp.Game;

/// <summary>
/// Who your retainers are, by id.
///
/// The game holds the list from the moment you log in, but Gleam only used to learn it while capturing a
/// snapshot. Anything drawn before the first scan of a session therefore had nothing to show, so a saved
/// layout fell back to printing a raw id and the settings page claimed no retainers existed. Names are read
/// straight from the game here, and remembered in the configuration so a retainer that is not currently
/// available -- another character's, or one you have dismissed -- still reads as a name rather than a number.
///
/// Two threads meet here: <see cref="Poll"/> runs on the game's own thread, because that is the only place
/// game memory may be read, while a scan finishing on a background thread reports what it saw. Everything
/// therefore goes through one lock, and readers get a copy rather than a live view.
/// </summary>
public static class RetainerDirectory
{
    private static readonly object gate = new();
    private static readonly Dictionary<ulong, string> live = new();
    private static double lastLook = double.NegativeInfinity;

    /// <summary>Every retainer Gleam can name: what the game says now, over what it said before.</summary>
    public static IReadOnlyDictionary<ulong, string> Names(Configuration config)
    {
        lock (gate)
        {
            var merged = new Dictionary<ulong, string>(config.KnownRetainerNames);
            foreach (var (id, name) in live) merged[id] = name;
            return merged;
        }
    }

    /// <summary>The retainers of the character you are on, as the game lists them now. Empty until it has.</summary>
    public static IReadOnlyDictionary<ulong, string> Current()
    {
        lock (gate) return new Dictionary<ulong, string>(live);
    }

    /// <summary>At logout, so the next character never inherits the last one's retainers.</summary>
    public static void ForgetLive()
    {
        lock (gate)
        {
            live.Clear();
            lastLook = double.NegativeInfinity;
        }
    }

    /// <summary>One retainer's name, or a plain description when Gleam has never seen it.</summary>
    public static string Name(Configuration config, ulong retainerId) =>
        Names(config).TryGetValue(retainerId, out var name) ? name : "a retainer Gleam has not met";

    /// <summary>
    /// Records names learned some other way, such as a scan that reached another character's storage.
    /// Returns whether anything was new, so the caller can decide whether the configuration is worth saving.
    /// </summary>
    public static bool Learn(Configuration config, IReadOnlyDictionary<ulong, string> names)
    {
        lock (gate)
        {
            var changed = false;
            foreach (var (id, name) in names)
            {
                if (id == 0 || string.IsNullOrEmpty(name)) continue;
                if (config.KnownRetainerNames.TryGetValue(id, out var had) && had == name) continue;
                config.KnownRetainerNames[id] = name;
                changed = true;
            }
            return changed;
        }
    }

    /// <summary>
    /// Reads the game's own retainer list, at most once a second. Call it only from the game's thread. A
    /// failure leaves the last answer in place: a name is never worth breaking a frame over.
    /// </summary>
    public static bool Poll(Configuration config)
    {
        var now = Environment.TickCount64 / 1000.0;
        lock (gate)
        {
            if (now - lastLook < 1.0) return false;
            lastLook = now;
        }

        IReadOnlyDictionary<ulong, string> found;
        try
        {
            found = GameInventoryScanner.KnownRetainers();
        }
        catch
        {
            return false;
        }
        if (found.Count == 0) return false;

        lock (gate)
        {
            live.Clear();
            foreach (var (id, name) in found) live[id] = name;
        }
        return Learn(config, found);
    }
}
