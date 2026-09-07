namespace TidyUp.Core.Model;

/// <summary>Static, per-item-id facts from the game sheets. Built once per item id by the adapter and cached.</summary>
public sealed record ItemInfo(
    uint ItemId,
    string Name,
    uint IconId,
    uint VendorPrice,
    uint BuyPrice,
    bool IsUntradable,
    bool IsUnique,
    bool IsIndisposable,
    bool CanBeHq,
    byte Rarity,
    byte LevelEquip,
    ushort ItemLevel,
    uint UiCategoryId,
    string UiCategory,
    uint ClassJobCategoryId,
    bool IsEquipment,
    bool IsDesynthable,
    uint StackSize,
    bool IsDyeable,
    bool IsMarketable,
    bool IsVendorBuyable,
    bool IsUsable = false)
{
    /// <summary>Stackable, undyed, non-equipment things are what the stack merge pass may combine.</summary>
    public bool IsStackable => StackSize > 1;

    public static readonly IReadOnlySet<string> MaterialCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Stone", "Metal", "Lumber", "Cloth", "Leather", "Bone", "Reagent", "Dye", "Part", "Ingredient",
        "Seafood", "Catalyst",
    };

    public static readonly IReadOnlySet<string> ConsumableCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Meal", "Medicine",
    };

    /// <summary>Categories no rule may ever propose, whatever the thresholds say.</summary>
    public static readonly IReadOnlySet<string> NeverProposeCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Currency", "Crystal", "Minion", "Mount", "Orchestrion Roll", "Soul Crystal", "Registrable Miscellany",
        "Furnishing", "Outdoor Furnishing", "Interior Fixture", "Exterior Fixture", "Gardening", "Painting",
        "Tabletop", "Wall-mounted", "Rug", "Airship/Submersible Component", "Fashion Accessory", "Triple Triad Card",
        "Orchestrion Components", "Facewear", "Chair/Bed", "Table", "Seasonal Miscellany",
    };

    public bool IsMaterial => MaterialCategories.Contains(UiCategory);
    public bool IsConsumable => ConsumableCategories.Contains(UiCategory);
    public bool IsNeverProposed => NeverProposeCategories.Contains(UiCategory);

    public static ItemInfo Test(uint id, string name, uint vendor = 0, bool marketable = true, bool equipment = false,
        byte levelEquip = 1, ushort ilvl = 1, string category = "Miscellany", bool untradable = false, bool unique = false,
        bool indisposable = false, uint stack = 0, byte rarity = 1, uint cjc = 0, bool vendorBuyable = false, bool usable = false) =>
        new(id, name, 0, vendor, 0, untradable, unique, indisposable, true, rarity, levelEquip, ilvl, 0, category,
            cjc, equipment, equipment, stack == 0 ? (equipment ? 1u : 999u) : stack, false, marketable && !untradable, vendorBuyable, usable);
}
