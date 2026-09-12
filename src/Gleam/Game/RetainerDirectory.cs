using Gleam.Core.Model;
using Gleam.Services;

namespace Gleam.Game;

/// <summary>
/// Who your retainers are, by id.
///
/// The game holds the list from the moment you log in, but Gleam only used to learn it while capturing a
/// snapshot. Anything drawn before the first scan of a session therefore had nothing to show, so a saved
/// layout fell back to printing a raw id and the settings page claimed no retainers existed. Names are read
/// straight from the game here, and remembered in the configuration so a retainer that is not currently
/// available -- another character's, or one you have dismissed -- still reads as a name rather than a number.
///
/// The game only lists a character's retainers once a summoning bell has been used since logging in. Until then
/// the list is empty, which made every retainer look like someone else's. So whose each retainer is gets
/// remembered too, and stands in for the game's list until the game has one.
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

    /// <summary>The character logged in, as last polled. 0 before login and after logout.</summary>
    private static ulong character;

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

    /// <summary>
    /// The retainers of the character you are on: as the game lists them, or before it has, the ones Gleam saw
    /// this character own. Empty only for a character whose retainers Gleam has never seen.
    /// </summary>
    public static IReadOnlyDictionary<ulong, string> Current(Configuration config)
    {
        lock (gate) return RetainerRoster.Mine(live, config.KnownRetainerNames, config.RetainerOwners, character);
    }

    /// <summary>
    /// Whether a retainer is known to belong to another character. Once the game lists this character's retainers,
    /// anything not on it is someone else's or dismissed. Before then only a remembered owner decides, and a
    /// retainer whose owner Gleam never saw is not claimed either way.
    /// </summary>
    public static bool IsSomeoneElses(Configuration config, ulong retainerId)
    {
        lock (gate)
            return live.Count > 0
                ? !live.ContainsKey(retainerId)
                : RetainerRoster.BelongsToSomeoneElse(retainerId, config.RetainerOwners, character);
    }

    /// <summary>At logout, so the next character never inherits the last one's retainers.</summary>
    public static void ForgetLive()
    {
        lock (gate)
        {
            live.Clear();
            character = 0;
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
    /// Reads the game's own retainer list, at most once a second, and remembers that the retainers on it belong
    /// to <paramref name="contentId"/>. Call it only from the game's thread. A failure leaves the last answer in
    /// place: a name is never worth breaking a frame over.
    /// </summary>
    public static bool Poll(Configuration config, ulong contentId)
    {
        var now = Environment.TickCount64 / 1000.0;
        lock (gate)
        {
            character = contentId;
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

        var owned = false;
        lock (gate)
        {
            live.Clear();
            foreach (var (id, name) in found) live[id] = name;
            // The game's list is this character's, so the next login knows them before any bell.
            if (contentId != 0)
                foreach (var id in found.Keys)
                {
                    if (config.RetainerOwners.TryGetValue(id, out var had) && had == contentId) continue;
                    config.RetainerOwners[id] = contentId;
                    owned = true;
                }
        }
        var learned = Learn(config, found);
        return learned || owned;
    }
}
