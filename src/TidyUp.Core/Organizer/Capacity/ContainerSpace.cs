using TidyUp.Core.Model;

namespace TidyUp.Core.Organizer.Capacity;

/// <summary>Identifies one storage the organizer can fill: a kind plus, for retainers, which one.</summary>
public readonly record struct StorageId(ContainerKind Kind, ulong OwnerId = 0)
{
    public static StorageId Of(SlotRef slot) => new(slot.Kind, slot.Kind == ContainerKind.Retainer ? slot.OwnerId : 0);
    public override string ToString() => OwnerId == 0 ? Kind.ToString() : $"{Kind}@{OwnerId:X}";
}

/// <summary>
/// How full one storage is and how much more it can take. Slots are counted per page; stack headroom is
/// tracked per (item, HQ) so an incoming stack merges into partial stacks before it costs a new slot.
/// Mutable so the solver can simulate a sequence of moves against it.
/// </summary>
public sealed class ContainerSpace
{
    private readonly Dictionary<uint, int> pageSize = new();
    private readonly Dictionary<uint, int> pageUsed = new();
    private readonly Dictionary<(uint ItemId, bool Hq), int> headroom = new();

    public StorageId Id { get; }

    /// <summary>True when sizes came from the live game rather than defaults.</summary>
    public bool SizesAreLive { get; init; }

    public ContainerSpace(StorageId id)
    {
        Id = id;
    }

    public int Size => pageSize.Values.Sum();
    public int Used => pageUsed.Values.Sum();
    public int Free => Size - Used;
    public IEnumerable<uint> Pages => pageSize.Keys;

    public int FreeOnPage(uint page) => pageSize.GetValueOrDefault(page) - pageUsed.GetValueOrDefault(page);

    public void SetPageSize(uint page, int size)
    {
        pageSize[page] = size;
        pageUsed.TryAdd(page, 0);
    }

    /// <summary>Registers an item that is already there.</summary>
    public void Occupy(ScannedItem item, ItemInfo? info)
    {
        pageUsed[item.Slot.ContainerId] = pageUsed.GetValueOrDefault(item.Slot.ContainerId) + 1;
        pageSize.TryAdd(item.Slot.ContainerId, 0);
        if (info is not null && info.StackSize > 1 && !item.IsCollectable && !item.HasMateria)
        {
            var key = (item.ItemId, item.IsHq);
            headroom[key] = headroom.GetValueOrDefault(key) + Math.Max(0, (int)info.StackSize - item.Quantity);
        }
    }

    /// <summary>Space left in partial stacks of this item.</summary>
    public int Headroom(uint itemId, bool hq) => headroom.GetValueOrDefault((itemId, hq));

    /// <summary>
    /// New slots an incoming stack would need after filling partial stacks. Zero when it merges away entirely.
    /// </summary>
    public int SlotsNeeded(uint itemId, bool hq, int quantity, uint stackSize, bool canMerge = true)
    {
        if (stackSize <= 1 || !canMerge) return 1;
        var room = Headroom(itemId, hq);
        var left = Math.Max(0, quantity - room);
        return left == 0 ? 0 : (int)Math.Ceiling(left / (double)stackSize);
    }

    /// <summary>Whether the stack fits right now, on any page, given the current headroom.</summary>
    public bool Fits(uint itemId, bool hq, int quantity, uint stackSize, bool canMerge = true) =>
        SlotsNeeded(itemId, hq, quantity, stackSize, canMerge) <= Free;

    /// <summary>Simulates the stack arriving: consumes headroom first, then free slots. Returns slots consumed.</summary>
    public int Accept(uint itemId, bool hq, int quantity, uint stackSize, bool canMerge = true)
    {
        var needed = SlotsNeeded(itemId, hq, quantity, stackSize, canMerge);
        if (stackSize > 1 && canMerge)
        {
            var key = (itemId, hq);
            var room = headroom.GetValueOrDefault(key);
            var merged = Math.Min(room, quantity);
            headroom[key] = room - merged;
            var left = quantity - merged;
            if (left > 0)
            {
                // The last new stack may be partial and becomes headroom for later arrivals.
                var partial = left % (int)stackSize;
                if (partial != 0) headroom[key] = headroom.GetValueOrDefault(key) + ((int)stackSize - partial);
            }
        }
        var remaining = needed;
        foreach (var page in pageSize.Keys.OrderBy(p => p))
        {
            while (remaining > 0 && FreeOnPage(page) > 0)
            {
                pageUsed[page]++;
                remaining--;
            }
        }
        if (remaining > 0) throw new InvalidOperationException($"{Id} has no room for {quantity} of item {itemId}");
        return needed;
    }

    /// <summary>Simulates the stack leaving: frees its slot and drops the headroom it offered.</summary>
    public void Release(ScannedItem item, ItemInfo? info)
    {
        var page = item.Slot.ContainerId;
        if (pageUsed.GetValueOrDefault(page) > 0) pageUsed[page]--;
        if (info is not null && info.StackSize > 1 && !item.IsCollectable && !item.HasMateria)
        {
            var key = (item.ItemId, item.IsHq);
            headroom[key] = Math.Max(0, headroom.GetValueOrDefault(key) - Math.Max(0, (int)info.StackSize - item.Quantity));
        }
    }

    public ContainerSpace Clone()
    {
        var c = new ContainerSpace(Id) { SizesAreLive = SizesAreLive };
        foreach (var (k, v) in pageSize) c.pageSize[k] = v;
        foreach (var (k, v) in pageUsed) c.pageUsed[k] = v;
        foreach (var (k, v) in headroom) c.headroom[k] = v;
        return c;
    }
}

/// <summary>Builds the space model for every storage in a snapshot, from live sizes where known and defaults otherwise.</summary>
public static class CapacityModel
{
    /// <summary>Slots per page when the game has not told us: bags 35, armoury 35 (rings 50), saddlebag 35, retainer 25.</summary>
    public static int DefaultPageSize(uint page)
    {
        if (page <= GameContainerIds.Inventory4) return 35;
        if (page == GameContainerIds.ArmoryRings) return 50;
        if (page >= GameContainerIds.ArmoryOffHand && page <= GameContainerIds.ArmoryMainHand) return 35;
        if (page >= GameContainerIds.SaddleBag1 && page <= GameContainerIds.PremiumSaddleBag2) return 35;
        if (page >= GameContainerIds.RetainerPage1 && page <= GameContainerIds.RetainerPage7) return 25;
        return 0;
    }

    /// <summary>The pages a storage is made of. Premium saddlebag pages are included only when live sizes report them.</summary>
    public static IEnumerable<uint> PagesOf(ContainerKind kind) => kind switch
    {
        ContainerKind.Inventory => GameContainerIds.InventoryPages,
        ContainerKind.Armoury => GameContainerIds.ArmouryPages,
        ContainerKind.Saddlebag => [GameContainerIds.SaddleBag1, GameContainerIds.SaddleBag2],
        ContainerKind.Retainer => GameContainerIds.RetainerPages,
        _ => [],
    };

    /// <summary>
    /// One <see cref="ContainerSpace"/> per storage that appears in <paramref name="items"/> or <paramref name="storages"/>.
    /// <paramref name="liveSizes"/> gives (storage, page) → size for containers the game has open; others use defaults.
    /// </summary>
    public static Dictionary<StorageId, ContainerSpace> Build(
        IEnumerable<ScannedItem> items,
        IEnumerable<StorageId> storages,
        Func<uint, ItemInfo?> info,
        IReadOnlyDictionary<(StorageId, uint), int>? liveSizes = null)
    {
        var spaces = new Dictionary<StorageId, ContainerSpace>();
        ContainerSpace Space(StorageId id)
        {
            if (spaces.TryGetValue(id, out var s)) return s;
            var live = liveSizes is not null && liveSizes.Keys.Any(k => k.Item1 == id);
            s = new ContainerSpace(id) { SizesAreLive = live };
            foreach (var page in PagesOf(id.Kind))
            {
                var size = liveSizes is not null && liveSizes.TryGetValue((id, page), out var sz) ? sz : DefaultPageSize(page);
                if (size > 0) s.SetPageSize(page, size);
            }
            if (liveSizes is not null)
                foreach (var ((sid, page), size) in liveSizes)
                    if (sid == id && !s.Pages.Contains(page) && size > 0) s.SetPageSize(page, size);
            spaces[id] = s;
            return s;
        }

        foreach (var id in storages) Space(id);
        foreach (var item in items)
        {
            if (item.Slot.Kind == ContainerKind.GlamourDresser) continue;
            Space(StorageId.Of(item.Slot)).Occupy(item, info(item.ItemId));
        }
        return spaces;
    }
}
