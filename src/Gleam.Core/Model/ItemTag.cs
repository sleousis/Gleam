namespace Gleam.Core.Model;

/// <summary>Coarse item types the confirmation window can filter by. Derived from the game's UI category.</summary>
public enum ItemTag
{
    Gear,
    Materia,
    Materials,
    Consumables,
    Crystals,
    Housing,
    Collectibles,
    Other,
}

public static class ItemTags
{
    private static readonly HashSet<string> Housing = new(StringComparer.OrdinalIgnoreCase)
    {
        "Furnishing", "Outdoor Furnishing", "Interior Fixture", "Exterior Fixture", "Gardening", "Painting",
        "Tabletop", "Wall-mounted", "Rug", "Chair/Bed", "Table", "Airship/Submersible Component",
    };

    private static readonly HashSet<string> Collectibles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Minion", "Mount", "Orchestrion Roll", "Orchestrion Components", "Triple Triad Card", "Fashion Accessory",
        "Facewear", "Registrable Miscellany",
    };

    public static ItemTag Of(ItemInfo info)
    {
        if (info.IsEquipment) return ItemTag.Gear;
        if (string.Equals(info.UiCategory, "Materia", StringComparison.OrdinalIgnoreCase)) return ItemTag.Materia;
        if (string.Equals(info.UiCategory, "Crystal", StringComparison.OrdinalIgnoreCase)) return ItemTag.Crystals;
        if (info.IsMaterial) return ItemTag.Materials;
        if (info.IsConsumable) return ItemTag.Consumables;
        if (Housing.Contains(info.UiCategory)) return ItemTag.Housing;
        if (Collectibles.Contains(info.UiCategory)) return ItemTag.Collectibles;
        return ItemTag.Other;
    }

    public static string Label(this ItemTag tag) => tag switch
    {
        ItemTag.Gear => "Gear",
        ItemTag.Materia => "Materia",
        ItemTag.Materials => "Materials",
        ItemTag.Consumables => "Consumables",
        ItemTag.Crystals => "Crystals",
        ItemTag.Housing => "Housing",
        ItemTag.Collectibles => "Collectibles",
        _ => "Other",
    };
}
