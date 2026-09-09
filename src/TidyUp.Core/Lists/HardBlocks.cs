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
    /// <summary>On the curated list of things that can never be regained (Ultimate tokens and weapons, the special earrings). Never shown, never touched.</summary>
    Protected,
}

public static class HardBlocks
{
    /// <summary>
    /// Reasons that make an item physically or irreversibly untouchable: the game refuses, or a gearset or
    /// plate would silently break. Everything else only keeps the *rules* away; the player can still pick the
    /// item by hand, with the reason shown as a warning.
    /// </summary>
    public static bool IsImmovable(HardBlockReason reason) =>
        reason is HardBlockReason.Indisposable or HardBlockReason.InGearset or HardBlockReason.InGlamourPlate or HardBlockReason.Protected or HardBlockReason.Currency;

    /// <summary>Ultimate weapons by shape rather than by id: blue rarity with three materia slots, outside the Bozjan relic range.</summary>
    public static bool IsUltimateWeapon(ItemInfo info) =>
        info.IsEquipment && info.Rarity == 3 && info.MateriaSlotCount == 3 && (info.ItemId < 33154 || info.ItemId > 33358);

    public static HardBlockReason Check(ScannedItem item, ItemInfo info, ItemContext ctx)
    {
        if (info.IsIndisposable) return HardBlockReason.Indisposable;
        if (info.UiCategory.Equals("Currency", StringComparison.OrdinalIgnoreCase)) return HardBlockReason.Currency;
        if (ctx.ProtectedItemIds.Contains(info.ItemId) || IsUltimateWeapon(info)) return HardBlockReason.Protected;

        // A spare copy of something already registered (minion, mount, roll, card...) is plain clutter; the
        // guards below exist to protect things that cannot be regained, which does not apply to it.
        var registeredSpare = ctx.Registered.TryGetValue(info.ItemId, out var registered) && registered;

        if (info.IsUnique && info.IsUntradable && !registeredSpare) return HardBlockReason.UniqueUntradeable;
        if (info.IsNeverProposed && !registeredSpare) return HardBlockReason.NeverProposedCategory;

        // A Fantasia is untradeable, sells for nothing, and is used by no recipe: it looks exactly like junk
        // to every heuristic. Anything in that shape can only be destroyed by being on the curated seasonal
        // list, which is a deliberate human decision rather than a rule's guess.
        if (!info.IsEquipment && info.IsUntradable && info.VendorPrice == 0 && !ctx.SeasonalItemIds.Contains(info.ItemId) && !registeredSpare)
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
        HardBlockReason.Indisposable => "Cannot be discarded",
        HardBlockReason.UniqueUntradeable => "Unique and untradeable; can never be reacquired",
        HardBlockReason.InGearset => "In a gear set",
        HardBlockReason.InGlamourPlate => "Used by a glamour plate",
        HardBlockReason.NeverProposedCategory => "Never suggested by any rule",
        HardBlockReason.Currency => "Currency",
        HardBlockReason.IrreplaceableUntradeable => "Untradeable with no vendor value; cannot be bought back",
        HardBlockReason.Protected => "Can never be regained; Tidy Up never touches it",
        _ => string.Empty,
    };
}
