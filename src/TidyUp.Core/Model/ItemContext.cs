namespace TidyUp.Core.Model;

/// <summary>A recipe that consumes an item, reduced to what the rules need.</summary>
public readonly record struct RecipeUse(uint CraftJobId, int RequiredLevel);

public readonly record struct MarketPrice(uint ItemId, long MinNq, long MinHq, DateTimeOffset FetchedAt)
{
    public long MinFor(bool hq) => hq && MinHq > 0 ? MinHq : MinNq;
}

/// <summary>
/// Everything about the *player* (not the item) that rules need, resolved once per scan.
/// Built by the game adapter; constructed directly in tests.
/// </summary>
public sealed record ItemContext
{
    public ulong CharacterId { get; init; }
    public string CharacterName { get; init; } = string.Empty;

    /// <summary>Base item ids referenced by any gearset.</summary>
    public IReadOnlySet<uint> GearsetItemIds { get; init; } = new HashSet<uint>();

    /// <summary>Base item ids referenced by any glamour plate. Empty when plates have not been loaded this session.</summary>
    public IReadOnlySet<uint> PlateItemIds { get; init; } = new HashSet<uint>();

    /// <summary>True only when plate data was actually read this session; the dresser rule refuses to run otherwise.</summary>
    public bool PlatesLoaded { get; init; }

    /// <summary>ClassJob row id → level for the current character.</summary>
    public IReadOnlyDictionary<uint, short> JobLevels { get; init; } = new Dictionary<uint, short>();

    /// <summary>ClassJobCategory row id → ClassJob row ids it contains.</summary>
    public IReadOnlyDictionary<uint, IReadOnlyList<uint>> ClassJobCategoryJobs { get; init; } =
        new Dictionary<uint, IReadOnlyList<uint>>();

    /// <summary>False when the gear sets could not be read. Then any piece of gear might be in one.</summary>
    public bool GearsetsKnown { get; init; } = true;

    /// <summary>Highest item level across all gearsets; the yardstick for "outleveled" consumables.</summary>
    public int MaxGearsetItemLevel { get; init; }

    public Func<uint, IReadOnlyList<RecipeUse>> RecipesUsing { get; init; } = _ => Array.Empty<RecipeUse>();

    public IReadOnlySet<uint> SeasonalItemIds { get; init; } = new HashSet<uint>();

    /// <summary>Curated ids that can never be regained once lost; they are never listed.</summary>
    public IReadOnlySet<uint> ProtectedItemIds { get; init; } = new HashSet<uint>();
    public IReadOnlySet<uint> RetiredCurrencyGearIds { get; init; } = new HashSet<uint>();

    public IReadOnlyDictionary<uint, MarketPrice> MarketPrices { get; init; } = new Dictionary<uint, MarketPrice>();

    /// <summary>True when a market lookup was attempted this scan; a marketable item with no price then means "unknown", not "worthless".</summary>
    public bool MarketLookupAttempted { get; init; }

    /// <summary>For items that register something (minions, mounts, orchestrion rolls, cards...): whether this character already has it.</summary>
    public IReadOnlyDictionary<uint, bool> Registered { get; init; } = new Dictionary<uint, bool>();

    /// <summary>
    /// The same context with market prices and registration added. A copy made by hand once dropped the
    /// curated never-touch ids on every real scan, so every other field is carried over by the compiler.
    /// </summary>
    public ItemContext WithMarket(IReadOnlyDictionary<uint, MarketPrice> prices, bool attempted, IReadOnlyDictionary<uint, bool> registered) =>
        this with { MarketPrices = prices, MarketLookupAttempted = attempted, Registered = registered };

    /// <summary>Highest level of any combat, crafting, or gathering job.</summary>
    public short MaxJobLevel => JobLevels.Count == 0 ? (short)0 : JobLevels.Values.Max();

    public short MaxLevelForCategory(uint classJobCategoryId)
    {
        if (classJobCategoryId == 0 || !ClassJobCategoryJobs.TryGetValue(classJobCategoryId, out var jobs) || jobs.Count == 0)
            return MaxJobLevel;
        short best = 0;
        foreach (var job in jobs)
            if (JobLevels.TryGetValue(job, out var lvl) && lvl > best) best = lvl;
        return best;
    }

    public bool AnyJobPlayedForCategory(uint classJobCategoryId) => MaxLevelForCategory(classJobCategoryId) > 1;

    public short LevelOf(uint classJobId) => JobLevels.TryGetValue(classJobId, out var lvl) ? lvl : (short)0;
}
