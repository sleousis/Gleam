using Dalamud.Plugin.Services;
using Gleam.Core.Integrations;
using Gleam.Core.Lists;
using Gleam.Core.Model;
using Gleam.Game;

namespace Gleam.Services;

/// <summary>Everything a planner needs about the character's storage at one moment.</summary>
public sealed record InventorySnapshot(
    IReadOnlyList<ScannedItem> Items,
    ItemContext Context,
    IReadOnlyDictionary<ulong, string> RetainerNames,
    /// <summary>Which containers came from the live game rather than the cache, by (kind, owner).</summary>
    IReadOnlySet<(ContainerKind Kind, ulong OwnerId)> LiveContainers);

/// <summary>
/// One scan for every feature: live containers from the game, closed ones from the cache (live always
/// wins), plus the character context enriched with home-world market prices and collectible registration.
/// </summary>
public sealed class InventorySnapshotService
{
    private readonly IFramework framework;
    private readonly IPlayerState player;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly ItemDatabase db;
    private readonly GameInventoryScanner scanner;
    private readonly ItemContextBuilder contextBuilder;
    private readonly IOfflineInventorySource offline;
    private readonly IMarketPriceSource market;

    public InventorySnapshotService(IFramework framework, IPlayerState player, IPluginLog log, Configuration config, ItemDatabase db,
        GameInventoryScanner scanner, ItemContextBuilder contextBuilder, IOfflineInventorySource offline, IMarketPriceSource market)
    {
        this.framework = framework;
        this.player = player;
        this.log = log;
        this.config = config;
        this.db = db;
        this.scanner = scanner;
        this.contextBuilder = contextBuilder;
        this.offline = offline;
        this.market = market;
    }

    /// <summary>Where market prices come from: the character's home world, e.g. "Omega". Empty until a lookup ran.</summary>
    public string MarketScope { get; private set; } = string.Empty;

    /// <summary>Scans everything the profile enables, or only <paramref name="focus"/> live, without cached containers.</summary>
    public async Task<InventorySnapshot> CaptureAsync(Profile profile, ContainerKind? focus = null)
    {
        var (live, ctx, retainerNames) = await framework.RunOnFrameworkThread(() =>
        {
            var items = focus is null
                ? scanner.ScanAll(profile.IsContainerEnabled(ContainerKind.Saddlebag), profile.IsContainerEnabled(ContainerKind.Retainer), profile.IsContainerEnabled(ContainerKind.GlamourDresser))
                : scanner.ScanKind(focus.Value);
            return (items, contextBuilder.Build(), GameInventoryScanner.KnownRetainers());
        }).ConfigureAwait(false);

        var liveContainers = new HashSet<(ContainerKind, ulong)>(live.Select(i => (i.Slot.Kind, i.Slot.OwnerId)));
        var all = new List<ScannedItem>(live);
        if (focus is null) all.AddRange(OfflineItems(live, ctx.CharacterId));

        var enriched = await EnrichAsync(all, ctx).ConfigureAwait(false);
        return new InventorySnapshot(all, enriched, retainerNames, liveContainers);
    }

    /// <summary>
    /// Everything the cache knows about this character that is not live right now: the saddlebag when
    /// closed, and every retainer's pages. Retainers are separate entries in the cache, recognised by
    /// their items living in retainer pages that name them as owner.
    /// </summary>
    private IEnumerable<ScannedItem> OfflineItems(IReadOnlyList<ScannedItem> live, ulong characterId)
    {
        if (!offline.IsAvailable) yield break;
        var liveKinds = new HashSet<(ContainerKind, ulong)>(live.Select(i => (i.Slot.Kind, i.Slot.OwnerId)));

        IEnumerable<ScannedItem> Filter(IEnumerable<ScannedItem> items)
        {
            foreach (var item in items)
            {
                // Never mix a live container with its cached copy; live always wins.
                if (item.Slot.Kind.IsAlwaysLoaded()) continue;
                if (item.Slot.Kind == ContainerKind.Retainer && item.Slot.OwnerId == 0) continue;
                if (liveKinds.Contains((item.Slot.Kind, item.Slot.OwnerId))) continue;
                if (item.Slot.Kind == ContainerKind.Saddlebag && GameInventoryScanner.IsSaddlebagLoaded()) continue;
                yield return item;
            }
        }

        foreach (var item in Filter(offline.Items(characterId))) yield return item;

        foreach (var entry in offline.Characters())
        {
            if (entry.CharacterId == characterId) continue;
            var items = offline.Items(entry.CharacterId);
            // A retainer's cache entry holds only retainer pages owned by that same id.
            if (items.Count == 0 || !items.All(i => i.Slot.Kind == ContainerKind.Retainer && i.Slot.OwnerId == entry.CharacterId)) continue;
            foreach (var item in Filter(items)) yield return item;
        }
    }

    /// <summary>Adds collectible registration and home-world market prices to the context.</summary>
    private async Task<ItemContext> EnrichAsync(IReadOnlyList<ScannedItem> items, ItemContext ctx)
    {
        var distinct = items.Select(i => i.ItemId).Distinct().ToList();

        var registered = await framework.RunOnFrameworkThread(() =>
        {
            var map = new Dictionary<uint, bool>();
            foreach (var id in distinct)
                if (UnlockState.Of(id) is { } state) map[id] = state;
            return map;
        }).ConfigureAwait(false);

        IReadOnlyDictionary<uint, MarketPrice> prices = new Dictionary<uint, MarketPrice>();
        var lookedUp = false;
        if (config.UseUniversalis)
        {
            var world = player.HomeWorld.ValueNullable?.Name.ExtractText();
            if (string.IsNullOrEmpty(world)) world = player.CurrentWorld.ValueNullable?.Name.ExtractText();
            var ids = distinct.Where(id => db.Get(id)?.IsMarketable == true).ToList();
            if (!string.IsNullOrEmpty(world) && ids.Count > 0)
            {
                MarketScope = world;
                lookedUp = true;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    prices = await market.GetPricesAsync(ids, world, cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.Debug(ex, "Market prices unavailable; rows will say so");
                }
            }
        }

        return ctx.WithMarket(prices, lookedUp, registered);
    }
}
