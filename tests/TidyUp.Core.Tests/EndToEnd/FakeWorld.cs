using TidyUp.Core.Execution;
using TidyUp.Core.Model;
using TidyUp.Core.Organizer.Capacity;
using TidyUp.Core.Organizer.Execution;

namespace TidyUp.Core.Tests.EndToEnd;

/// <summary>
/// One in-memory game for end-to-end scenarios: bags, armoury, saddlebag, retainers, the dresser, NPC
/// windows and market slots, all in one consistent state that both the cleaner and the organizer drive.
/// Windows open and close like in the game: one retainer at a time, the saddlebag on request, materia
/// retrieval refused while a retainer is summoned. Chaos hooks let scenarios break things mid-run.
/// </summary>
internal sealed class FakeWorld : IGameActions, IMoveActions
{
    public const uint MateriaItemId = 5000;

    public Dictionary<SlotRef, ScannedItem> Slots { get; } = new();

    // ---- what is open right now
    public ulong ActiveRetainer { get; private set; }
    public bool SaddlebagOpen { get; set; }
    public bool DresserOpen { get; set; }
    public bool ShopOpen { get; set; }
    public bool GrandCompanyOpen { get; set; }

    // ---- capacities
    public int MarketCapacity { get; set; } = 20;
    /// <summary>Page sizes that differ from the game defaults; reported as live sizes to the capacity model.</summary>
    public Dictionary<(StorageId Storage, uint Page), int> PageSizes { get; } = new();

    // ---- what happened
    public List<string> Calls { get; } = new();
    public List<(ulong Retainer, uint ItemId, int Quantity, long UnitPrice)> Listings { get; } = new();
    public List<(uint ItemId, int Quantity)> Destroyed { get; } = new();
    public List<(uint ItemId, int Quantity)> Sold { get; } = new();
    public List<(uint ItemId, int Quantity)> TurnedIn { get; } = new();
    public int MaxBagOccupancy { get; private set; }

    // ---- chaos
    public Func<SlotRef, bool> FailDiscardWhen { get; set; } = _ => false;
    public Func<SlotRef, SlotRef, bool> RefuseMove { get; set; } = (_, _) => false;
    /// <summary>Runs right before every destructive action or move; scenarios use it to change the world mid-run.</summary>
    public Action? BeforeAction { get; set; }

    public string? LastFailure { get; private set; }

    // ------------------------------------------------------------------ setup helpers

    public ScannedItem Add(SlotRef slot, uint itemId, int qty, bool hq = false, params ushort[] materia)
    {
        var item = ScannedItem.Simple(slot, itemId, qty, hq, OwnerName(slot)) with { Materia = materia };
        Slots[slot] = item;
        return item;
    }

    public void OpenRetainer(ulong id) => ActiveRetainer = id;
    public void LeaveBell() => ActiveRetainer = 0;

    public IReadOnlyList<ScannedItem> Items => Slots.Values.OrderBy(i => i.Slot.Kind).ThenBy(i => i.Slot.OwnerId).ThenBy(i => i.Slot.ContainerId).ThenBy(i => i.Slot.Slot).ToList();

    public int Count(ContainerKind kind, ulong owner = 0) => Slots.Values.Count(i => i.Slot.Kind == kind && (owner == 0 || i.Slot.OwnerId == owner));
    public int CountOf(uint itemId) => Slots.Values.Where(i => i.ItemId == itemId).Sum(i => i.Quantity);
    public bool Has(uint itemId) => Slots.Values.Any(i => i.ItemId == itemId);
    public bool Has(SlotRef slot) => Slots.ContainsKey(slot);

    public int BagOccupancy => Slots.Values.Count(i => i.Slot.Kind == ContainerKind.Inventory);
    public int BagCapacity => GameContainerIds.InventoryPages.Sum(p => SizeOf(new StorageId(ContainerKind.Inventory), p));

    private int SizeOf(StorageId storage, uint page) => PageSizes.TryGetValue((storage, page), out var s) ? s : CapacityModel.DefaultPageSize(page);

    private static string OwnerName(SlotRef slot) => slot.Kind == ContainerKind.Retainer ? $"Retainer {slot.OwnerId:X}" : string.Empty;

    private SlotRef? FirstFree(StorageId storage, uint? onlyPage = null, IReadOnlySet<SlotRef>? reserved = null)
    {
        var pages = onlyPage is { } p ? [p] : CapacityModel.PagesOf(storage.Kind).ToArray();
        foreach (var page in pages)
        {
            var size = SizeOf(storage, page);
            for (var i = 0; i < size; i++)
            {
                var s = new SlotRef(storage.Kind, page, i, storage.OwnerId);
                if (!Slots.ContainsKey(s) && (reserved is null || !reserved.Contains(s))) return s;
            }
        }
        return null;
    }

    private void Track()
    {
        MaxBagOccupancy = Math.Max(MaxBagOccupancy, BagOccupancy);
    }

    // ------------------------------------------------------------------ IGameActions

    public bool IsContainerAvailable(ContainerKind kind, ulong ownerId) => kind switch
    {
        ContainerKind.Inventory or ContainerKind.Armoury => true,
        ContainerKind.Saddlebag => SaddlebagOpen,
        ContainerKind.Retainer => ActiveRetainer != 0 && ActiveRetainer == ownerId,
        ContainerKind.GlamourDresser => DresserOpen,
        _ => false,
    };

    public ScannedItem? ReadSlot(SlotRef slot) => Slots.GetValueOrDefault(slot);

    public SlotRef? FindSlot(ContainerKind kind, ulong ownerId, uint itemId, int quantity, bool isHq, IReadOnlySet<SlotRef> exclude, SlotRef preferred)
    {
        var hits = Slots.Values.Where(i => i.Slot.Kind == kind && (kind != ContainerKind.Retainer || i.Slot.OwnerId == ownerId)
                                          && i.ItemId == itemId && i.Quantity == quantity && i.IsHq == isHq && !exclude.Contains(i.Slot)).ToList();
        if (hits.Count == 0) return null;
        return (hits.FirstOrDefault(h => h.Slot == preferred) ?? hits[0]).Slot;
    }

    public int FreeInventorySlots() => BagCapacity - BagOccupancy;

    public bool IsActionAvailable(ActionKind action) => action switch
    {
        ActionKind.VendorSell => ShopOpen || ActiveRetainer != 0,
        ActionKind.MarketList => ActiveRetainer != 0,
        ActionKind.ExpertDelivery => GrandCompanyOpen,
        _ => true,
    };

    public string ActionRequirement(ActionKind action) => action switch
    {
        ActionKind.VendorSell => "sell to a merchant or through a retainer",
        ActionKind.MarketList => "list from a retainer's sell menu",
        ActionKind.ExpertDelivery => "turn in at your Grand Company",
        _ => string.Empty,
    };

    public bool CanRetrieveMateriaIn(ContainerKind kind) => kind is ContainerKind.Inventory or ContainerKind.Armoury && ActiveRetainer == 0;

    private Task<bool> Remove(string name, SlotRef slot, uint itemId, List<(uint, int)> into, Func<bool>? gate = null)
    {
        BeforeAction?.Invoke();
        Calls.Add($"{name}:{slot}");
        LastFailure = null;
        if (gate is not null && !gate()) { LastFailure = "the window is not open"; return Task.FromResult(false); }
        if (!Slots.TryGetValue(slot, out var item) || item.ItemId != itemId) { LastFailure = "the slot changed"; return Task.FromResult(false); }
        if (name == "discard" && FailDiscardWhen(slot)) { LastFailure = "the game refused"; return Task.FromResult(false); }
        Slots.Remove(slot);
        into.Add((itemId, item.Quantity));
        return Task.FromResult(true);
    }

    public Task<bool> DiscardAsync(SlotRef slot, uint itemId, CancellationToken ct) => Remove("discard", slot, itemId, Destroyed);
    public Task<bool> VendorSellAsync(SlotRef slot, uint itemId, CancellationToken ct) => Remove("sell", slot, itemId, Sold, () => IsActionAvailable(ActionKind.VendorSell));
    public Task<bool> ExpertDeliveryAsync(SlotRef slot, uint itemId, CancellationToken ct) => Remove("seals", slot, itemId, TurnedIn, () => GrandCompanyOpen);
    public Task<bool> DesynthAsync(SlotRef slot, uint itemId, CancellationToken ct) => Remove("desynth", slot, itemId, Destroyed);

    public Task<SlotRef?> RestoreFromDresserAsync(SlotRef dresserSlot, uint itemId, CancellationToken ct)
    {
        Calls.Add($"restore:{dresserSlot}");
        if (!DresserOpen || !Slots.TryGetValue(dresserSlot, out var item) || item.ItemId != itemId) { LastFailure = "the dresser is not open"; return Task.FromResult<SlotRef?>(null); }
        var landing = FirstFree(new StorageId(ContainerKind.Inventory));
        if (landing is null) { LastFailure = "no bag space"; return Task.FromResult<SlotRef?>(null); }
        Slots.Remove(dresserSlot);
        Slots[landing.Value] = item with { Slot = landing.Value };
        Track();
        return Task.FromResult<SlotRef?>(landing);
    }

    public Task<SlotRef?> MoveToInventoryAsync(SlotRef slot, uint itemId, int quantity, bool isHq, CancellationToken ct)
    {
        BeforeAction?.Invoke();
        Calls.Add($"home:{slot}");
        if (!Slots.TryGetValue(slot, out var item) || item.ItemId != itemId) { LastFailure = "the slot changed"; return Task.FromResult<SlotRef?>(null); }
        if (!IsContainerAvailable(slot.Kind, slot.OwnerId)) { LastFailure = "that container is not open"; return Task.FromResult<SlotRef?>(null); }
        var landing = FirstFree(new StorageId(ContainerKind.Inventory));
        if (landing is null) { LastFailure = "no bag space"; return Task.FromResult<SlotRef?>(null); }
        Slots.Remove(slot);
        Slots[landing.Value] = item with { Slot = landing.Value, OwnerName = string.Empty };
        Track();
        return Task.FromResult<SlotRef?>(landing);
    }

    public Task<bool> RetrieveMateriaAsync(SlotRef slot, uint itemId, CancellationToken ct)
    {
        Calls.Add($"materia:{slot}");
        LastFailure = null;
        if (!CanRetrieveMateriaIn(slot.Kind)) { LastFailure = "Retrieve Materia is not offered here"; return Task.FromResult(false); }
        if (!Slots.TryGetValue(slot, out var item) || item.ItemId != itemId) { LastFailure = "the slot changed"; return Task.FromResult(false); }
        var count = item.MateriaCount;
        if (FreeInventorySlots() < count) { LastFailure = "no bag space for the materia"; return Task.FromResult(false); }
        // One materia comes off per request in the real game; the engine asks once per item, so drop them all here.
        for (var i = 0; i < count; i++)
        {
            var landing = FirstFree(new StorageId(ContainerKind.Inventory))!.Value;
            Slots[landing] = ScannedItem.Simple(landing, MateriaItemId, 1);
        }
        Slots[slot] = item with { Materia = Array.Empty<ushort>() };
        Track();
        return Task.FromResult(true);
    }

    public Task<bool> MarketListAsync(SlotRef slot, uint itemId, long unitPrice, int quantity, CancellationToken ct)
    {
        BeforeAction?.Invoke();
        Calls.Add($"list@{unitPrice}x{quantity}:{slot}");
        LastFailure = null;
        if (ActiveRetainer == 0) { LastFailure = "no retainer is open"; return Task.FromResult(false); }
        if (FreeMarketSlots() <= 0) { LastFailure = "no free market slots"; return Task.FromResult(false); }
        if (!Slots.TryGetValue(slot, out var item) || item.ItemId != itemId || item.Quantity < quantity) { LastFailure = "the slot changed"; return Task.FromResult(false); }
        if (quantity < item.Quantity) Slots[slot] = item with { Quantity = item.Quantity - quantity };
        else Slots.Remove(slot);
        Listings.Add((ActiveRetainer, itemId, quantity, unitPrice));
        return Task.FromResult(true);
    }

    public int FreeMarketSlots() => ActiveRetainer == 0 ? 0 : MarketCapacity - Listings.Count(l => l.Retainer == ActiveRetainer);

    // ------------------------------------------------------------------ IMoveActions

    public bool IsOpen(StorageId storage) => IsContainerAvailable(storage.Kind, storage.OwnerId);

    public SlotRef? FindSlot(StorageId storage, uint itemId, int quantity, bool isHq, IReadOnlySet<SlotRef> exclude, SlotRef? preferred)
    {
        var hits = Slots.Values.Where(i => StorageId.Of(i.Slot) == storage && i.ItemId == itemId && i.Quantity == quantity && i.IsHq == isHq && !exclude.Contains(i.Slot)).Select(i => i.Slot).ToList();
        if (hits.Count == 0) return null;
        return preferred is { } p && hits.Contains(p) ? p : hits[0];
    }

    public SlotRef? FindLanding(StorageId storage, uint itemId, bool isHq, uint preferredPage, IReadOnlySet<SlotRef> reserved)
    {
        var info = E2e.Lookup(itemId);
        if (info is { IsStackable: true })
        {
            var partial = Slots.Values.FirstOrDefault(i => StorageId.Of(i.Slot) == storage && i.ItemId == itemId && i.IsHq == isHq && i.Quantity < info.StackSize && !reserved.Contains(i.Slot));
            if (partial is not null) return partial.Slot;
        }
        return FirstFree(storage, preferredPage != 0 ? preferredPage : null, reserved);
    }

    public IReadOnlyDictionary<(StorageId Storage, uint Page), int> LiveSizes() => PageSizes;

    public Task<MoveOutcome> MoveAsync(SlotRef from, SlotRef to, uint itemId, int quantity, CancellationToken ct)
    {
        BeforeAction?.Invoke();
        Calls.Add($"move:{from}->{to}");
        if (RefuseMove(from, to)) return Task.FromResult(new MoveOutcome(MoveStatus.Refused, "the game refused the move"));
        if (!Slots.TryGetValue(from, out var item) || item.ItemId != itemId || item.Quantity != quantity)
            return Task.FromResult(new MoveOutcome(MoveStatus.SourceChanged, "the stack is not there any more"));
        if (!IsOpen(StorageId.Of(from)) || !IsOpen(StorageId.Of(to)))
            return Task.FromResult(new MoveOutcome(MoveStatus.Refused, "that storage is not open"));

        Slots.Remove(from);
        if (Slots.TryGetValue(to, out var existing))
        {
            var info = E2e.Lookup(itemId);
            if (existing.ItemId != itemId || existing.IsHq != item.IsHq || info is null || existing.Quantity + item.Quantity > info.StackSize)
            {
                Slots[from] = item;
                return Task.FromResult(new MoveOutcome(MoveStatus.Refused, "the destination slot is taken"));
            }
            Slots[to] = existing with { Quantity = existing.Quantity + item.Quantity };
        }
        else
        {
            Slots[to] = item with { Slot = to, OwnerName = OwnerName(to) };
        }
        Track();
        return Task.FromResult(MoveOutcome.Ok);
    }
}
