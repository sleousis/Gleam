using TidyUp.Core.Model;

namespace TidyUp.Core.Integrations;

public sealed record OfflineCharacter(ulong CharacterId, string Name, bool IsRetainer, ulong OwnerCharacterId);

/// <summary>Closed containers seen through a cache. Allagan Tools is the v1 implementation; tests use a fake.</summary>
public interface IOfflineInventorySource
{
    bool IsAvailable { get; }
    IReadOnlyList<OfflineCharacter> Characters();
    IReadOnlyList<ScannedItem> Items(ulong characterOrRetainerId);
}

public sealed class NullOfflineInventorySource : IOfflineInventorySource
{
    public bool IsAvailable => false;
    public IReadOnlyList<OfflineCharacter> Characters() => Array.Empty<OfflineCharacter>();
    public IReadOnlyList<ScannedItem> Items(ulong characterOrRetainerId) => Array.Empty<ScannedItem>();
}

/// <summary>
/// Decodes Allagan Tools' <c>ulong[]</c> item records (CriticalCommonLib InventoryItem.ToNumeric):
/// [0] container, [1] slot, [2] itemId, [3] quantity, [4] spiritbond, [5] condition, [6] flags,
/// [7..11] materia ids, [12..16] materia grades, [17] stain, [18] stain2, [19] glamourId,
/// [20] sortedContainer, [21] sortedCategory, [22] sortedSlotIndex, [23] retainerId, [24] retainerMarketPrice.
/// </summary>
public static class AllaganItemRecord
{
    private const ulong FlagHq = 1;
    private const ulong FlagCollectable = 8;

    public static ScannedItem? Parse(ulong[] rec, string ownerName)
    {
        if (rec.Length < 24) return null;
        var container = (uint)rec[0];
        var kind = GameContainerIds.KindOf(container);
        if (kind is null) return null;

        var rawItemId = (uint)rec[2];
        var qty = (int)rec[3];
        if (rawItemId == 0 || qty <= 0) return null;

        var flags = rec[6];
        var hq = (flags & FlagHq) != 0 || ScannedItem.IsHqItemId(rawItemId);
        var collectable = (flags & FlagCollectable) != 0;
        var materia = new ushort[5];
        for (var i = 0; i < 5; i++) materia[i] = (ushort)rec[7 + i];
        var owner = kind == ContainerKind.Retainer ? rec[23] : 0;

        return new ScannedItem(
            new SlotRef(kind.Value, container, (int)rec[1], owner),
            ScannedItem.BaseItemId(rawItemId),
            qty,
            hq,
            collectable,
            materia,
            (byte)rec[17],
            (byte)rec[18],
            (uint)rec[4],
            ownerName);
    }
}
