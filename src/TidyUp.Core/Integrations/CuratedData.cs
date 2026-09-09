using System.Text.Json;
using System.Text.Json.Serialization;

namespace TidyUp.Core.Integrations;

/// <summary>Hand-maintained lists shipped as JSON next to the plugin. Updated per patch.</summary>
public sealed class CuratedData
{
    [JsonPropertyName("version")] public string Version { get; set; } = "0";
    [JsonPropertyName("updatedForPatch")] public string UpdatedForPatch { get; set; } = string.Empty;

    /// <summary>Item ids of consumables and tokens from seasonal events that have ended.</summary>
    [JsonPropertyName("seasonalItemIds")] public List<uint> SeasonalItemIds { get; set; } = new();

    /// <summary>English names of currencies that can no longer be earned (scrips; tomestones are derived from game data).</summary>
    [JsonPropertyName("retiredCurrencyNames")] public List<string> RetiredCurrencyNames { get; set; } = new();

    /// <summary>Items that can never be regained once lost (Ultimate tokens, the special earrings...). Never listed, not even for hand-picking.</summary>
    [JsonPropertyName("protectedItemIds")] public List<uint> ProtectedItemIds { get; set; } = new();

    public static CuratedData Empty => new();

    public static CuratedData Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<CuratedData>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? Empty;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }
}

/// <summary>What a Discard Helper configuration says: what it throws away, and what it protects.</summary>
public sealed record DiscardHelperLists(IReadOnlyList<uint> Discard, IReadOnlyList<uint> Keep)
{
    public static DiscardHelperLists Empty { get; } = new(Array.Empty<uint>(), Array.Empty<uint>());
    public bool IsEmpty => Discard.Count == 0 && Keep.Count == 0;
}

/// <summary>
/// Reads a Discard Helper (ARDiscard) configuration. Its two lists mean opposite things: "DiscardingItems"
/// is what it throws away, "BlacklistedItems" is what it must never touch. Mixing them up would turn a
/// player's protected items into junk, so the names are matched exactly rather than guessed at.
/// </summary>
public static class DiscardHelperImport
{
    public static DiscardHelperLists Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return DiscardHelperLists.Empty;
            return new DiscardHelperLists(Ids(doc.RootElement, "DiscardingItems"), Ids(doc.RootElement, "BlacklistedItems"));
        }
        catch (JsonException)
        {
            return DiscardHelperLists.Empty;
        }
    }

    /// <summary>
    /// Writes the two lists back out in Discard Helper's own shape, so its owner can carry them the other
    /// way. Only the fields that hold the lists are written; Discard Helper fills in the rest itself.
    /// </summary>
    public static string Write(DiscardHelperLists lists)
    {
        var doc = new
        {
            type = "ARDiscard.Configuration, ARDiscard",
            Version = 3,
            DiscardingItems = lists.Discard,
            BlacklistedItems = lists.Keep,
            ExcludedCharacters = Array.Empty<object>(),
        };
        var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        // Its type marker is a "$type" property, which C# cannot name directly.
        return json.Replace("\"type\":", "\"$type\":");
    }

    private static IReadOnlyList<uint> Ids(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            return Array.Empty<uint>();
        var ids = new List<uint>();
        foreach (var el in array.EnumerateArray())
            if (el.ValueKind == JsonValueKind.Number && el.TryGetUInt32(out var id) && id is > 0 and < 1_000_000)
                ids.Add(id);
        return ids;
    }
}
