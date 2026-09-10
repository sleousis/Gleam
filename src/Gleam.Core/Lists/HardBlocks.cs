using Gleam.Core.Model;

namespace Gleam.Core.Lists;

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
    /// <summary>The gear sets could not be read, so any piece of gear might belong to one.</summary>
    GearsetsUnknown,
    /// <summary>The glamour plates have not been read yet, so any dresser item might be on one.</summary>
    PlatesUnknown,
}

public static class HardBlocks
{
    /// <summary>
    /// Reasons that make an item physically or irreversibly untouchable: the game refuses, or a gearset or
    /// plate would silently break. Everything else only keeps the *rules* away; the player can still pick the
    /// item by hand, with the reason shown as a warning.
    /// </summary>
    public static bool IsImmovable(HardBlockReason reason) =>
        reason is HardBlockReason.Indisposable or HardBlockReason.InGearset or HardBlockReason.InGlamourPlate or HardBlockReason.Protected or HardBlockReason.Currency
            or HardBlockReason.GearsetsUnknown or HardBlockReason.PlatesUnknown;

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

        // When the gear sets could not be read, any piece of gear might be in one. An empty set used to mean
        // "in none", so every gear set piece lost its protection on a bad read.
        if (item.Slot.Kind != ContainerKind.GlamourDresser && info.IsEquipment && !ctx.GearsetsKnown)
            return HardBlockReason.GearsetsUnknown;

        // Until the plates have been read, any dresser item might be on one.
        if (item.Slot.Kind == ContainerKind.GlamourDresser && !ctx.PlatesLoaded)
            return HardBlockReason.PlatesUnknown;

        // Plates reference dresser items; restoring one silently breaks the plate.
        if (item.Slot.Kind == ContainerKind.GlamourDresser && ctx.PlateItemIds.Contains(info.ItemId))
            return HardBlockReason.InGlamourPlate;

        return HardBlockReason.None;
    }

    public static string Describe(HardBlockReason reason) => reason switch
    {
        HardBlockReason.Indisposable => "Cannot be discarded",
        HardBlockReason.UniqueUntradeable => "Unique and untradeable. Can never be reacquired",
        HardBlockReason.InGearset => "In a gear set",
        HardBlockReason.InGlamourPlate => "Used by a glamour plate",
        HardBlockReason.NeverProposedCategory => "Never suggested by any rule",
        HardBlockReason.Currency => "Currency",
        HardBlockReason.IrreplaceableUntradeable => "Untradeable with no vendor value. Cannot be bought back",
        HardBlockReason.Protected => "Can never be regained. Gleam never touches it",
        HardBlockReason.GearsetsUnknown => "Your gear sets could not be read, so Gleam leaves all gear alone this time",
        HardBlockReason.PlatesUnknown => "Open the glamour dresser once so Gleam can see which pieces your plates use",
        _ => string.Empty,
    };
}
