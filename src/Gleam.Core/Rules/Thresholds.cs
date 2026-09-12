namespace Gleam.Core.Rules;

/// <summary>What a preset does with the items it proposes. Stated in one sentence each, and shown to the user as such.</summary>
public enum ActionPolicy
{
    /// <summary>Each rule picks the action it thinks best (seals, desynth, sell, discard).</summary>
    RuleDecides,
    /// <summary>Retired: the old Cautious preset. Kept so saved configs still load; migrated to Balanced on startup.</summary>
    SellOnly,
    /// <summary>Untradeable items are discarded; tradeable items are sold.</summary>
    DiscardUntradeableSellTradeable,
    /// <summary>Everything proposed is discarded.</summary>
    DiscardAll,
    /// <summary>Marketable items go on the market board at the lowest home-world price; other tradeable items are vendored; untradeable ones are discarded.</summary>
    MarketListTradeable,
}

public static class ActionPolicyExtensions
{
    public static string Describe(this ActionPolicy p) => p switch
    {
        ActionPolicy.SellOnly => "Sells tradeable items to a vendor, never discards.",
        ActionPolicy.DiscardUntradeableSellTradeable => "Sells tradeable items to a vendor, discards the rest.",
        ActionPolicy.DiscardAll => "Discards everything listed.",
        ActionPolicy.MarketListTradeable => "Lists marketable items, sells the rest to a vendor, discards untradeable ones.",
        _ => "Each rule picks its own action.",
    };
}

/// <summary>Every tunable number the rules and the run caps read. Mutable so it round-trips through plugin config.</summary>
public sealed class Thresholds
{
    /// <summary>What happens to proposed items. Presets set this; it is the headline meaning of each preset.</summary>
    public ActionPolicy Policy { get; set; } = ActionPolicy.DiscardUntradeableSellTradeable;

    /// <summary>Gear is obsolete when the best job that can wear it is at least this many levels above the gear's equip level.</summary>
    public int ObsoleteGearLevelGap { get; set; } = 15;

    /// <summary>Also propose gear for job categories where no job has ever been levelled (all at level 1).</summary>
    public bool IncludeGearForUnplayedJobs { get; set; } = false;

    /// <summary>Food and medicine are outleveled when their item level is this far below the best gearset item level.</summary>
    public int ConsumableItemLevelGap { get; set; } = 200;

    /// <summary>Crafting materials count as unusable when every recipe using them is at or below this level ...</summary>
    public int CraftingMatMaxRecipeLevel { get; set; } = 50;

    /// <summary>... and the relevant crafter is at least this far above that level.</summary>
    public int CraftingMatCrafterLeadLevels { get; set; } = 10;

    /// <summary>Vendor-only junk must be worth at most this much per unit to be proposed.</summary>
    public uint VendorOnlyMaxUnitPrice { get; set; } = 500;

    /// <summary>Market beats vendor when market×(1-tax) exceeds vendor×this factor.</summary>
    public double MarketPremiumFactor { get; set; } = 1.5;

    /// <summary>Only bother flagging market value when the whole stack is worth at least this much.</summary>
    public long MarketMinStackValueGil { get; set; } = 5000;

    /// <summary>Market board tax fraction used when comparing to vendor price.</summary>
    public double MarketTaxRate { get; set; } = 0.05;

    public int SoftCapItems { get; set; } = 50;
    public long SoftCapGil { get; set; } = 50_000;

    public Thresholds Clone() => (Thresholds)MemberwiseClone();
}

public enum PresetName
{
    /// <summary>Sell on the market board: list marketable items via retainers, vendor the rest, discard untradeable.</summary>
    MarketBoard,
    /// <summary>Sell to vendors: vendor tradeable items, discard untradeable.</summary>
    Vendor,
    /// <summary>Discard everything proposed.</summary>
    DiscardAll,
    Custom,
}

public static class PresetNameExtensions
{
    public static string Label(this PresetName p) => p switch
    {
        PresetName.MarketBoard => "Sell on market board",
        PresetName.Vendor => "Sell to vendors",
        PresetName.DiscardAll => "Discard all",
        _ => "Custom",
    };
}

public static class Presets
{
    public static Thresholds For(PresetName preset) => preset switch
    {
        PresetName.MarketBoard => new Thresholds { Policy = ActionPolicy.MarketListTradeable },
        // The three presets differ only in what happens to junk, which is all their labels promise. "Discard all"
        // used to widen what counted as junk and raise the big-run limits as well, without saying so.
        PresetName.DiscardAll => new Thresholds { Policy = ActionPolicy.DiscardAll },
        _ => new Thresholds(),
    };

    /// <summary>"Discard all" as saved before 0.12, when it also widened what counted as junk.</summary>
    private static readonly Thresholds LegacyDiscardAll = new()
    {
        Policy = ActionPolicy.DiscardAll,
        ObsoleteGearLevelGap = 10,
        IncludeGearForUnplayedJobs = true,
        ConsumableItemLevelGap = 120,
        CraftingMatMaxRecipeLevel = 70,
        CraftingMatCrafterLeadLevels = 5,
        VendorOnlyMaxUnitPrice = 2000,
        MarketPremiumFactor = 2.0,
        MarketMinStackValueGil = 10_000,
        SoftCapItems = 100,
        SoftCapGil = 150_000,
    };

    /// <summary>Whether a saved profile is still on the old "Discard all" values, to be moved to the new ones.</summary>
    public static bool IsLegacyDiscardAll(Thresholds t) => Equal(LegacyDiscardAll, t);

    /// <summary>Which preset a threshold set matches exactly, or Custom.</summary>
    public static PresetName Detect(Thresholds t)
    {
        foreach (var p in new[] { PresetName.MarketBoard, PresetName.Vendor, PresetName.DiscardAll })
            if (Equal(For(p), t)) return p;
        return PresetName.Custom;
    }

    private static bool Equal(Thresholds a, Thresholds b) =>
        a.Policy == b.Policy &&
        a.ObsoleteGearLevelGap == b.ObsoleteGearLevelGap &&
        a.IncludeGearForUnplayedJobs == b.IncludeGearForUnplayedJobs &&
        a.ConsumableItemLevelGap == b.ConsumableItemLevelGap &&
        a.CraftingMatMaxRecipeLevel == b.CraftingMatMaxRecipeLevel &&
        a.CraftingMatCrafterLeadLevels == b.CraftingMatCrafterLeadLevels &&
        a.VendorOnlyMaxUnitPrice == b.VendorOnlyMaxUnitPrice &&
        Math.Abs(a.MarketPremiumFactor - b.MarketPremiumFactor) < 0.0001 &&
        a.MarketMinStackValueGil == b.MarketMinStackValueGil &&
        Math.Abs(a.MarketTaxRate - b.MarketTaxRate) < 0.0001 &&
        a.SoftCapItems == b.SoftCapItems &&
        a.SoftCapGil == b.SoftCapGil;
}
