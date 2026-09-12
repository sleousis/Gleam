namespace Gleam.Core.Model;

/// <summary>
/// Which retainers belong to the character you are on. The game lists a character's retainers only once a summoning
/// bell has been used since logging in, so until then the retainers Gleam saw that character own before stand in.
/// </summary>
public static class RetainerRoster
{
    /// <summary>The game's own list when it has one, otherwise the saved retainers owned by <paramref name="contentId"/>.</summary>
    public static IReadOnlyDictionary<ulong, string> Mine(
        IReadOnlyDictionary<ulong, string> live, IReadOnlyDictionary<ulong, string> saved,
        IReadOnlyDictionary<ulong, ulong> owners, ulong contentId)
    {
        if (live.Count > 0 || contentId == 0) return new Dictionary<ulong, string>(live);
        var mine = new Dictionary<ulong, string>();
        foreach (var (id, name) in saved)
            if (owners.TryGetValue(id, out var owner) && owner == contentId) mine[id] = name;
        return mine;
    }

    /// <summary>Whether a retainer is known to belong to another character. One whose owner was never seen is not claimed either way.</summary>
    public static bool BelongsToSomeoneElse(ulong retainerId, IReadOnlyDictionary<ulong, ulong> owners, ulong contentId) =>
        contentId != 0 && owners.TryGetValue(retainerId, out var owner) && owner != contentId;
}
