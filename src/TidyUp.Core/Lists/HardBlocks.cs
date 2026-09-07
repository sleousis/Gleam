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
}

public static class HardBlocks
{
    public static HardBlockReason Check(ScannedItem item, ItemInfo info, ItemContext ctx)
    {
        if (info.IsIndisposable) return HardBlockReason.Indisposable;
        if (info.IsUnique && info.IsUntradable) return HardBlockReason.UniqueUntradeable;
        if (info.IsNeverProposed) return HardBlockReason.NeverProposedCategory;

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
        HardBlockReason.Indisposable => "The game does not allow this item to be discarded",
        HardBlockReason.UniqueUntradeable => "Unique and untradeable: can never be reacquired",
        HardBlockReason.InGearset => "Referenced by a gearset",
        HardBlockReason.InGlamourPlate => "Referenced by a glamour plate",
        HardBlockReason.NeverProposedCategory => "Category Tidy Up never touches",
        HardBlockReason.Currency => "Currency",
        _ => string.Empty,
    };
}
