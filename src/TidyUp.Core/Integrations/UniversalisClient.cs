using System.Net.Http.Json;
using System.Text.Json;
using TidyUp.Core.Model;

namespace TidyUp.Core.Integrations;

public interface IMarketPriceSource
{
    Task<IReadOnlyDictionary<uint, MarketPrice>> GetPricesAsync(IEnumerable<uint> itemIds, string worldOrDataCenter, CancellationToken ct);
}

public sealed class NullMarketPriceSource : IMarketPriceSource
{
    public Task<IReadOnlyDictionary<uint, MarketPrice>> GetPricesAsync(IEnumerable<uint> itemIds, string worldOrDataCenter, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<uint, MarketPrice>>(new Dictionary<uint, MarketPrice>());
}

/// <summary>
/// Universalis v2 aggregated prices with a 15-minute cache and 100-id batches. Failures degrade to
/// "market unknown" for the affected ids; they never block a run.
/// </summary>
public sealed class UniversalisClient : IMarketPriceSource
{
    public const int BatchSize = 100;

    private readonly HttpClient http;
    private readonly TimeSpan cacheFor;
    private readonly Func<DateTimeOffset> now;
    private readonly Dictionary<(string, uint), MarketPrice> cache = new();
    private readonly object gate = new();

    public UniversalisClient(HttpClient? http = null, TimeSpan? cacheFor = null, Func<DateTimeOffset>? clock = null)
    {
        this.http = http ?? new HttpClient { BaseAddress = new Uri("https://universalis.app/") };
        if (this.http.BaseAddress is null) this.http.BaseAddress = new Uri("https://universalis.app/");
        this.http.DefaultRequestHeaders.UserAgent.ParseAdd("TidyUp/1.0 (Dalamud plugin)");
        this.cacheFor = cacheFor ?? TimeSpan.FromMinutes(15);
        now = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string? LastError { get; private set; }

    public async Task<IReadOnlyDictionary<uint, MarketPrice>> GetPricesAsync(IEnumerable<uint> itemIds, string worldOrDataCenter, CancellationToken ct)
    {
        var result = new Dictionary<uint, MarketPrice>();
        var missing = new List<uint>();
        var t = now();
        lock (gate)
        {
            foreach (var id in itemIds.Distinct())
            {
                if (cache.TryGetValue((worldOrDataCenter, id), out var p) && t - p.FetchedAt < cacheFor) result[id] = p;
                else missing.Add(id);
            }
        }

        foreach (var batch in missing.Chunk(BatchSize))
        {
            try
            {
                var ids = string.Join(',', batch);
                var url = $"api/v2/{Uri.EscapeDataString(worldOrDataCenter)}/{ids}?listings=0&entries=0&fields=items.minPriceNQ,items.minPriceHQ,itemID,minPriceNQ,minPriceHQ";
                using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    LastError = $"Universalis {(int)resp.StatusCode}";
                    continue;
                }
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);
                foreach (var price in ParseResponse(json, batch, t))
                {
                    result[price.ItemId] = price;
                    lock (gate) cache[(worldOrDataCenter, price.ItemId)] = price;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                LastError = ex.Message;
            }
        }

        return result;
    }

    /// <summary>Universalis returns a flat object for one id and <c>{ "items": { id: {...} } }</c> for several.</summary>
    public static IEnumerable<MarketPrice> ParseResponse(JsonElement json, IReadOnlyList<uint> requested, DateTimeOffset fetchedAt)
    {
        if (json.ValueKind != JsonValueKind.Object) yield break;

        if (json.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in items.EnumerateObject())
            {
                if (!uint.TryParse(prop.Name, out var id)) continue;
                yield return Read(prop.Value, id, fetchedAt);
            }
            yield break;
        }

        if (json.TryGetProperty("itemID", out var single) && single.TryGetUInt32(out var singleId))
            yield return Read(json, singleId, fetchedAt);
        else if (requested.Count == 1)
            yield return Read(json, requested[0], fetchedAt);
    }

    private static MarketPrice Read(JsonElement e, uint id, DateTimeOffset at)
    {
        long nq = 0, hq = 0;
        if (e.TryGetProperty("minPriceNQ", out var n) && n.ValueKind == JsonValueKind.Number) nq = n.GetInt64();
        if (e.TryGetProperty("minPriceHQ", out var h) && h.ValueKind == JsonValueKind.Number) hq = h.GetInt64();
        return new MarketPrice(id, nq, hq, at);
    }
}
