using Gleam.Core.Execution;
using Gleam.Core.Model;

namespace Gleam.Core.Planning;

/// <summary>What runs when the player presses the button while rows accepted earlier are still waiting for a closed container.</summary>
public static class PendingMerge
{
    /// <summary>An item wherever it now sits in its container: slots move, so a no is by item.</summary>
    public static (ContainerKind Kind, ulong OwnerId, uint ItemId, bool IsHq) Identity(SlotRef slot, uint itemId, bool hq) => (slot.Kind, slot.OwnerId, itemId, hq);

    /// <summary>
    /// Rows accepted earlier for a container that was closed ride along, unless the player has since said no to
    /// that item. An unticked row for the same item is a no, wherever the item now sits. A waiting row for a slot
    /// the new queue also acts on gives way to the new one. Waiting rows go first.
    /// </summary>
    public static List<QueuedAction> Merge(IEnumerable<QueuedAction> pending, IReadOnlyList<QueuedAction> queue, IEnumerable<PlanRow> rows)
    {
        var declined = rows.Where(r => !r.Checked).Select(r => Identity(r.Item.Slot, r.Item.ItemId, r.Item.IsHq)).ToHashSet();
        var carried = pending.Where(p => !queue.Any(q => q.Slot == p.Slot) && !declined.Contains(Identity(p.Slot, p.ItemId, p.IsHq)));
        return carried.Concat(queue).ToList();
    }
}
