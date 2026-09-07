using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using TidyUp.Core.Model;

namespace TidyUp.Game;

/// <summary>
/// Reads containers straight from the game's InventoryManager. Runs on the framework thread.
/// Dalamud's inventory helper hides some container types, retainer pages among them, so every read
/// here goes to the native container and reports whether that container is actually loaded.
/// </summary>
public sealed unsafe class GameInventoryScanner
{
    private readonly IPluginLog log;

    public GameInventoryScanner(IGameInventory inventory, IPluginLog log)
    {
        this.log = log;
    }

    public IReadOnlyList<ScannedItem> ScanAll(bool includeSaddlebag, bool includeRetainer, bool includeDresser)
    {
        var result = new List<ScannedItem>();
        Scan(result, GameContainerIds.InventoryPages, ContainerKind.Inventory, 0, string.Empty);
        Scan(result, GameContainerIds.ArmouryPages, ContainerKind.Armoury, 0, string.Empty);

        if (includeSaddlebag && IsSaddlebagLoaded())
            Scan(result, GameContainerIds.SaddlebagPages, ContainerKind.Saddlebag, 0, string.Empty);

        if (includeRetainer)
        {
            var (retainerId, retainerName) = ActiveRetainer();
            if (retainerId != 0)
                Scan(result, GameContainerIds.RetainerPages, ContainerKind.Retainer, retainerId, retainerName);
        }

        if (includeDresser) ScanDresser(result);
        return result;
    }

    public IReadOnlyList<ScannedItem> ScanKind(ContainerKind kind)
    {
        var result = new List<ScannedItem>();
        switch (kind)
        {
            case ContainerKind.Inventory: Scan(result, GameContainerIds.InventoryPages, kind, 0, ""); break;
            case ContainerKind.Armoury: Scan(result, GameContainerIds.ArmouryPages, kind, 0, ""); break;
            case ContainerKind.Saddlebag: if (IsSaddlebagLoaded()) Scan(result, GameContainerIds.SaddlebagPages, kind, 0, ""); break;
            case ContainerKind.Retainer:
                var (id, name) = ActiveRetainer();
                if (id != 0) Scan(result, GameContainerIds.RetainerPages, kind, id, name);
                break;
            case ContainerKind.GlamourDresser: ScanDresser(result); break;
        }
        return result;
    }

    private void Scan(List<ScannedItem> into, uint[] pages, ContainerKind kind, ulong ownerId, string ownerName)
    {
        var im = InventoryManager.Instance();
        if (im == null) return;
        foreach (var page in pages)
        {
            var container = im->GetInventoryContainer((InventoryType)page);
            if (container == null || !container->IsLoaded || container->Items == null) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var item = &container->Items[i];
                if (item->ItemId == 0 || item->GetQuantity() == 0) continue;
                into.Add(Convert(item, kind, page, i, ownerId, ownerName));
            }
        }
    }

    private static ScannedItem Convert(InventoryItem* item, ContainerKind kind, uint page, int slot, ulong ownerId, string ownerName)
    {
        var materia = new List<ushort>();
        for (byte i = 0; i < 5; i++)
        {
            var m = item->GetMateriaId(i);
            if (m != 0) materia.Add(m);
        }
        return new ScannedItem(
            new SlotRef(kind, page, slot, ownerId),
            ScannedItem.BaseItemId(item->ItemId),
            (int)item->GetQuantity(),
            item->IsHighQuality(),
            item->IsCollectable(),
            materia,
            item->GetStain(0),
            item->GetStain(1),
            item->GetSpiritbondOrCollectability(),
            ownerName);
    }

    /// <summary>Live read. Null when the slot is empty *or* its container is not loaded; use <see cref="TryReadSlot"/> to tell them apart.</summary>
    public ScannedItem? ReadSlot(SlotRef slot) => TryReadSlot(slot, out _);

    /// <summary>Live read that also says whether the container was available at all.</summary>
    public ScannedItem? TryReadSlot(SlotRef slot, out bool containerLoaded)
    {
        containerLoaded = false;
        if (slot.Kind == ContainerKind.GlamourDresser)
        {
            containerLoaded = IsDresserLoaded();
            return containerLoaded ? ReadDresserSlot(slot.Slot) : null;
        }

        var im = InventoryManager.Instance();
        if (im == null) return null;
        var container = im->GetInventoryContainer((InventoryType)slot.ContainerId);
        if (container == null || !container->IsLoaded || container->Items == null) return null;
        if (slot.Kind == ContainerKind.Retainer && !IsRetainerOpen(slot.OwnerId)) return null;
        containerLoaded = true;
        if (slot.Slot < 0 || slot.Slot >= container->Size) return null;

        var item = &container->Items[slot.Slot];
        if (item->ItemId == 0 || item->GetQuantity() == 0) return null;
        var ownerId = slot.Kind == ContainerKind.Retainer ? ActiveRetainer().Id : 0;
        return Convert(item, slot.Kind, slot.ContainerId, slot.Slot, ownerId, string.Empty);
    }

    public static bool IsSaddlebagLoaded()
    {
        var im = InventoryManager.Instance();
        if (im == null) return false;
        var c = im->GetInventoryContainer(InventoryType.SaddleBag1);
        return c != null && c->IsLoaded;
    }

    public static (ulong Id, string Name) ActiveRetainer()
    {
        var rm = RetainerManager.Instance();
        if (rm == null) return (0, string.Empty);
        var r = rm->GetActiveRetainer();
        if (r == null || r->RetainerId == 0) return (0, string.Empty);
        // The retainer is only really "open" once its first page is loaded.
        var im = InventoryManager.Instance();
        var page = im == null ? null : im->GetInventoryContainer(InventoryType.RetainerPage1);
        if (page == null || !page->IsLoaded) return (0, string.Empty);
        return (r->RetainerId, r->NameString);
    }

    public static bool IsRetainerOpen(ulong retainerId)
    {
        var (id, _) = ActiveRetainer();
        return id != 0 && retainerId != 0 && id == retainerId;
    }

    public static IReadOnlyDictionary<ulong, string> KnownRetainers()
    {
        var result = new Dictionary<ulong, string>();
        var rm = RetainerManager.Instance();
        if (rm == null) return result;
        foreach (ref var r in rm->Retainers)
            if (r.RetainerId != 0 && r.Available) result[r.RetainerId] = r.NameString;
        return result;
    }

    public static bool IsDresserLoaded()
    {
        var mm = MirageManager.Instance();
        return mm != null && mm->PrismBoxLoaded;
    }

    private void ScanDresser(List<ScannedItem> into)
    {
        var mm = MirageManager.Instance();
        if (mm == null || !mm->PrismBoxLoaded) return;
        var ids = mm->PrismBoxItemIds;
        var s0 = mm->PrismBoxStain0Ids;
        var s1 = mm->PrismBoxStain1Ids;
        for (var i = 0; i < ids.Length; i++)
        {
            var raw = ids[i];
            if (raw == 0) continue;
            into.Add(new ScannedItem(
                SlotRef.Dresser(i),
                ScannedItem.BaseItemId(raw),
                1,
                ScannedItem.IsHqItemId(raw),
                false,
                Array.Empty<ushort>(),
                i < s0.Length ? s0[i] : (byte)0,
                i < s1.Length ? s1[i] : (byte)0,
                0,
                string.Empty));
        }
    }

    private static ScannedItem? ReadDresserSlot(int index)
    {
        var mm = MirageManager.Instance();
        if (mm == null || !mm->PrismBoxLoaded) return null;
        var ids = mm->PrismBoxItemIds;
        if (index < 0 || index >= ids.Length || ids[index] == 0) return null;
        var raw = ids[index];
        return new ScannedItem(SlotRef.Dresser(index), ScannedItem.BaseItemId(raw), 1, ScannedItem.IsHqItemId(raw), false,
            Array.Empty<ushort>(), mm->PrismBoxStain0Ids[index], mm->PrismBoxStain1Ids[index], 0, string.Empty);
    }

    /// <summary>Base item ids referenced by any glamour plate, or null when plates are not loaded.</summary>
    public static HashSet<uint>? PlateItemIds()
    {
        var mm = MirageManager.Instance();
        if (mm == null || !mm->GlamourPlatesLoaded) return null;
        var set = new HashSet<uint>();
        foreach (ref var plate in mm->GlamourPlates)
            foreach (var id in plate.ItemIds)
                if (id != 0) set.Add(ScannedItem.BaseItemId(id));
        return set;
    }

    public static int FreeInventorySlots()
    {
        var im = InventoryManager.Instance();
        return im == null ? 0 : (int)im->GetEmptySlotsInBag();
    }

    public static int TotalInventorySlots()
    {
        var im = InventoryManager.Instance();
        if (im == null) return 140;
        var total = 0;
        foreach (var page in GameContainerIds.InventoryPages)
        {
            var c = im->GetInventoryContainer((InventoryType)page);
            if (c != null) total += c->Size;
        }
        return total == 0 ? 140 : total;
    }
}
