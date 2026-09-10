namespace Gleam.Core.Model;

/// <summary>A single physical stack observed in a container. Always carries the base item id (HQ flag stripped).</summary>
public sealed record ScannedItem(
    SlotRef Slot,
    uint ItemId,
    int Quantity,
    bool IsHq,
    bool IsCollectable,
    IReadOnlyList<ushort> Materia,
    byte Stain0,
    byte Stain1,
    uint Spiritbond,
    string OwnerName)
{
    public bool HasMateria => Materia.Count > 0 && Materia.Any(m => m != 0);

    public int MateriaCount => Materia.Count(m => m != 0);

    public bool IsDyed => Stain0 != 0 || Stain1 != 0;

    public static ScannedItem Simple(SlotRef slot, uint itemId, int qty, bool hq = false, string owner = "") =>
        new(slot, itemId, qty, hq, false, Array.Empty<ushort>(), 0, 0, 0, owner);

    /// <summary>Game item ids carry flags: +1,000,000 for HQ, +500,000 for collectable.</summary>
    public static uint BaseItemId(uint rawItemId)
    {
        if (rawItemId >= 1_000_000) return rawItemId - 1_000_000;
        if (rawItemId >= 500_000) return rawItemId - 500_000;
        return rawItemId;
    }

    public static bool IsHqItemId(uint rawItemId) => rawItemId >= 1_000_000 && rawItemId < 2_000_000;
}
