using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using TidyUp.Core.Model;

namespace TidyUp.Game;

/// <summary>Reads live containers into <see cref="ScannedItem"/>s. Runs on the framework thread.</summary>
public sealed unsafe class GameInventoryScanner
{
    private readonly IGameInventory inventory;
    private readonly IPluginLog log;

    public GameInventoryScanner(IGameInventory inventory, IPluginLog log)
    {
        this.inventory = inventory;
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
        foreach (var page in pages)
        {
            ReadOnlySpan<GameInventoryItem> span;
            try { span = inventory.GetInventoryItems((GameInventoryType)page); }
            catch (Exception ex) { log.Debug(ex, "Container {Page} unreadable", page); continue; }

            foreach (ref readonly var gi in span)
            {
                if (gi.IsEmpty || gi.ItemId == 0 || gi.Quantity <= 0) continue;
                into.Add(Convert(gi, kind, page, ownerId, ownerName));
            }
        }
    }

    public static ScannedItem Convert(in GameInventoryItem gi, ContainerKind kind, uint page, ulong ownerId, string ownerName)
    {
        var materia = new List<ushort>();
        foreach (var m in gi.Materia) if (m != 0) materia.Add(m);
        var stains = gi.Stains;
        return new ScannedItem(
            new SlotRef(kind, page, (int)gi.InventorySlot, ownerId),
            gi.BaseItemId,
            gi.Quantity,
            gi.IsHq,
            gi.IsCollectable,
            materia,
            stains.Length > 0 ? stains[0] : (byte)0,
            stains.Length > 1 ? stains[1] : (byte)0,
            gi.SpiritbondOrCollectability,
            ownerName);
    }

    public ScannedItem? ReadSlot(SlotRef slot)
    {
        if (slot.Kind == ContainerKind.GlamourDresser) return ReadDresserSlot(slot.Slot);
        ReadOnlySpan<GameInventoryItem> span;
        try { span = inventory.GetInventoryItems((GameInventoryType)slot.ContainerId); }
        catch { return null; }
        foreach (ref readonly var gi in span)
        {
            if (gi.InventorySlot != (uint)slot.Slot) continue;
            if (gi.IsEmpty || gi.ItemId == 0) return null;
            var ownerId = slot.Kind == ContainerKind.Retainer ? ActiveRetainer().Id : 0;
            return Convert(gi, slot.Kind, slot.ContainerId, ownerId, string.Empty);
        }
        return null;
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
        if (rm == null || !rm->IsReady) return (0, string.Empty);
        var r = rm->GetActiveRetainer();
        if (r == null || r->RetainerId == 0) return (0, string.Empty);
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
