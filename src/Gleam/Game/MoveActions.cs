using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Gleam.Core.Model;
using Gleam.Core.Organizer.Capacity;
using Gleam.Core.Organizer.Execution;

namespace Gleam.Game;

/// <summary>
/// Moves stacks between slots with the game's own inventory manager and waits until the slots agree that
/// it happened. Pointer work lives in static helpers; everything async is safe code.
/// </summary>
public sealed class MoveActions : IMoveActions
{
    private readonly IFramework framework;
    private readonly GameInventoryScanner scanner;
    private readonly IPluginLog log;
    private readonly Configuration config;

    private readonly Func<uint, uint> stackSizeOf;

    public MoveActions(IFramework framework, GameInventoryScanner scanner, IPluginLog log, Configuration config, Func<uint, uint> stackSizeOf)
    {
        this.stackSizeOf = stackSizeOf;
        this.framework = framework;
        this.scanner = scanner;
        this.log = log;
        this.config = config;
    }

    private TimeSpan Timeout => TimeSpan.FromMilliseconds(config.Callbacks.ActionTimeoutMs);

    // Data can stay loaded after a window closes; the game only accepts moves while the window itself is up.
    public bool IsOpen(StorageId storage) => storage.Kind switch
    {
        ContainerKind.Inventory or ContainerKind.Armoury => true,
        ContainerKind.Saddlebag => GameInventoryScanner.IsSaddlebagLoaded() && AddonDriver.IsAddonVisible("InventoryBuddy"),
        ContainerKind.Retainer => GameInventoryScanner.IsRetainerOpen(storage.OwnerId)
                                  && (AddonDriver.IsAddonVisible("InventoryRetainer") || AddonDriver.IsAddonVisible("InventoryRetainerLarge")),
        _ => false,
    };

    public ScannedItem? ReadSlot(SlotRef slot) => scanner.ReadSlot(slot);

    public SlotRef? FindSlot(StorageId storage, uint itemId, int quantity, bool isHq, IReadOnlySet<SlotRef> exclude, SlotRef? preferred)
    {
        var hits = scanner.ScanKind(storage.Kind)
            .Where(i => (storage.Kind != ContainerKind.Retainer || i.Slot.OwnerId == storage.OwnerId)
                        && i.ItemId == itemId && i.Quantity == quantity && i.IsHq == isHq && !exclude.Contains(i.Slot))
            .Select(i => i.Slot)
            .ToList();
        if (hits.Count == 0) return null;
        return preferred is { } p && hits.Contains(p) ? p : hits[0];
    }

    public SlotRef? FindLanding(StorageId storage, uint itemId, bool isHq, int quantity, uint preferredPage, IReadOnlySet<SlotRef> reserved, bool emptyOnly = false) =>
        Native.FindLanding(storage, itemId, isHq, quantity, stackSizeOf(itemId), preferredPage, reserved, emptyOnly);

    public IReadOnlyDictionary<(StorageId Storage, uint Page), int> LiveSizes() => GameInventoryScanner.LiveSizes();

    public async Task<MoveOutcome> MoveAsync(SlotRef from, SlotRef to, uint itemId, int quantity, CancellationToken ct)
    {
        var before = scanner.ReadSlot(from);
        if (before is null || before.ItemId != itemId || before.Quantity != quantity)
            return new MoveOutcome(MoveStatus.SourceChanged, "the stack is no longer where it was");
        var destBefore = scanner.ReadSlot(to);
        var expectAtDest = destBefore is null ? quantity : destBefore.ItemId == itemId ? destBefore.Quantity + quantity : -1;
        if (expectAtDest < 0) return new MoveOutcome(MoveStatus.Refused, "the destination slot holds a different item");
        // A merge must take the whole stack; the game would otherwise leave a remainder behind or swap the two.
        if (destBefore is not null && expectAtDest > stackSizeOf(itemId)) return new MoveOutcome(MoveStatus.Refused, "the stack there has no room for all of it");

        var sent = await framework.RunOnFrameworkThread(() => Native.Move(from, to, itemId)).ConfigureAwait(false);
        if (sent is null) return new MoveOutcome(MoveStatus.Refused, "the item or its destination could not be read");
        log.Debug("MoveItemSlot {From} -> {To} returned {Code}", from, to, sent.Value);

        // The slots are the ground truth: the source empties (or shrinks, on a merge) and the destination gains.
        // The call's return code is logged but not trusted either way.
        // The move has been sent, so it is confirmed on its own deadline whatever Stop says. Waiting on the
        // run's token reported a move that happened as cancelled, and kept it out of the move history.
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            var src = scanner.ReadSlot(from);
            var dst = scanner.ReadSlot(to);
            var sourceGone = src is null || src.ItemId != itemId || src.Quantity < quantity;
            var destHas = dst is not null && dst.ItemId == itemId && dst.Quantity >= Math.Min(expectAtDest, quantity);
            if (sourceGone && destHas) return MoveOutcome.Ok;
        }
        log.Debug("Move {From} -> {To} of item {Item} was sent (code {Code}) but not confirmed", from, to, itemId, sent.Value);
        return new MoveOutcome(MoveStatus.NotConfirmed, "the game did not carry out the move");
    }

    private static unsafe class Native
    {
        /// <summary>Issues the move. Null when the slots could not even be addressed; otherwise the game's return code, for the log.</summary>
        public static int? Move(SlotRef from, SlotRef to, uint itemId)
        {
            var im = InventoryManager.Instance();
            if (im == null) return null;
            var src = im->GetInventorySlot((InventoryType)from.ContainerId, from.Slot);
            if (src == null || ScannedItem.BaseItemId(src->ItemId) != itemId) return null;
            var dstContainer = im->GetInventoryContainer((InventoryType)to.ContainerId);
            if (dstContainer == null || !dstContainer->IsLoaded || to.Slot < 0 || to.Slot >= dstContainer->Size) return null;
            return im->MoveItemSlot((InventoryType)from.ContainerId, (ushort)from.Slot, (InventoryType)to.ContainerId, (ushort)to.Slot, true);
        }

        public static SlotRef? FindLanding(StorageId storage, uint itemId, bool isHq, int quantity, uint stackSize, uint preferredPage, IReadOnlySet<SlotRef> reserved, bool emptyOnly)
        {
            var im = InventoryManager.Instance();
            if (im == null) return null;
            var owner = storage.Kind == ContainerKind.Retainer ? storage.OwnerId : 0;
            var pages = CapacityModel.PagesOf(storage.Kind).ToList();
            if (storage.Kind == ContainerKind.Saddlebag) pages.AddRange([GameContainerIds.PremiumSaddleBag1, GameContainerIds.PremiumSaddleBag2]);
            if (preferredPage != 0) pages = pages.Where(p => p == preferredPage).Concat(pages.Where(p => p != preferredPage)).ToList();

            SlotRef? firstEmpty = null;
            foreach (var page in pages)
            {
                var c = im->GetInventoryContainer((InventoryType)page);
                if (c == null || !c->IsLoaded || c->Items == null) continue;
                for (var i = 0; i < c->Size; i++)
                {
                    var slot = new SlotRef(storage.Kind, page, i, owner);
                    if (reserved.Contains(slot)) continue;
                    var item = &c->Items[i];
                    if (item->ItemId == 0 || item->GetQuantity() == 0)
                    {
                        firstEmpty ??= slot;
                        continue;
                    }
                    if (!emptyOnly && ScannedItem.BaseItemId(item->ItemId) == itemId && item->IsHighQuality() == isHq && !item->IsCollectable()
                        && item->GetQuantity() + quantity <= stackSize)
                        return slot; // a stack of the same thing with room for all of it: the game merges into it
                }
                // For the armoury, only the item's own page is valid; stop after the preferred page.
                if (storage.Kind == ContainerKind.Armoury && preferredPage != 0) break;
            }
            return firstEmpty;
        }
    }
}
