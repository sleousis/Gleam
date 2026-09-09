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

/// <summary>
/// Reads an ARDiscard (Discard Helper) configuration and returns the item ids it would discard, so a
/// switcher can seed the always-discard list. The format is not documented, so this is deliberately
/// tolerant: any array of integers under a key mentioning "discard" or "item" counts.
/// </summary>
public static class DiscardHelperImport
{
    public static IReadOnlyList<uint> ParseItemIds(string json)
    {
        var ids = new HashSet<uint>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            Walk(doc.RootElement, null, ids);
        }
        catch (JsonException)
        {
            return Array.Empty<uint>();
        }
        return ids.OrderBy(i => i).ToList();
    }

    private static void Walk(JsonElement e, string? key, HashSet<uint> ids)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) Walk(p.Value, p.Name, ids);
                break;
            case JsonValueKind.Array:
                var relevant = key is not null &&
                               (key.Contains("discard", StringComparison.OrdinalIgnoreCase) ||
                                key.Contains("item", StringComparison.OrdinalIgnoreCase));
                foreach (var el in e.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.Number && relevant && el.TryGetUInt32(out var id) && id > 0 && id < 1_000_000)
                        ids.Add(id);
                    else if (el.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        Walk(el, key, ids);
                }
                break;
        }
    }
}
