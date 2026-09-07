using TidyUp.Core.Model;

namespace TidyUp.Core.Lists;

/// <summary>Reasons an item can never be proposed. These are not user-removable and win over the blacklist.</summary>
public enum HardBlockReason
{
    None,
    Indisposable,
    UniqueUntradeable,
    InGearset,
    InGlamourPlate,
    NeverProposedCategory,
    Currency,
    /// <summary>Untradeable, no vendor value, not equipment: Fantasia, tokens, vouchers. Cannot be bought back with gil.</summary>
    IrreplaceableUntradeable,
}

public static class HardBlocks
{
    /// <summary>
    /// Reasons that make an item physically or irreversibly untouchable: the game refuses, or a gearset or
    /// plate would silently break. Everything else only keeps the *rules* away; the player can still pick the
    /// item by hand, with the reason shown as a warning.
    /// </summary>
    public static bool IsImmovable(HardBlockReason reason) =>
        reason is HardBlockReason.Indisposable or HardBlockReason.InGearset or HardBlockReason.InGlamourPlate;

    public static HardBlockReason Check(ScannedItem item, ItemInfo info, ItemContext ctx)
    {
        if (info.IsIndisposable) return HardBlockReason.Indisposable;
        if (info.IsUnique && info.IsUntradable) return HardBlockReason.UniqueUntradeable;
        if (info.IsNeverProposed) return HardBlockReason.NeverProposedCategory;

        // A Fantasia is untradeable, sells for nothing, and is used by no recipe: it looks exactly like junk
        // to every heuristic. Anything in that shape can only be destroyed by being on the curated seasonal
        // list, which is a deliberate human decision rather than a rule's guess.
        if (!info.IsEquipment && info.IsUntradable && info.VendorPrice == 0 && !ctx.SeasonalItemIds.Contains(info.ItemId))
            return HardBlockReason.IrreplaceableUntradeable;

        // Gearsets reference armoury/inventory gear by item id; dresser copies are a separate physical item.
        if (item.Slot.Kind != ContainerKind.GlamourDresser && info.IsEquipment && ctx.GearsetItemIds.Contains(info.ItemId))
            return HardBlockReason.InGearset;

        // Plates reference dresser items; restoring one silently breaks the plate.
        if (item.Slot.Kind == ContainerKind.GlamourDresser && ctx.PlateItemIds.Contains(info.ItemId))
            return HardBlockReason.InGlamourPlate;

        return HardBlockReason.None;
    }

    public static string Describe(HardBlockReason reason) => reason switch
    {
        HardBlockReason.Indisposable => "The game does not allow this item to be discarded.",
        HardBlockReason.UniqueUntradeable => "Unique and untradeable. It can never be reacquired.",
        HardBlockReason.InGearset => "Part of a gearset.",
        HardBlockReason.InGlamourPlate => "Used by a glamour plate.",
        HardBlockReason.NeverProposedCategory => "In a category no rule ever proposes.",
        HardBlockReason.Currency => "Currency",
        HardBlockReason.IrreplaceableUntradeable => "Untradeable with no vendor value. It cannot be bought back.",
        _ => string.Empty,
    };
}
