using Gleam.Core.Model;
using Gleam.Core.Organizer.Capacity;
using Gleam.Core.Organizer.Solving;

namespace Gleam.Core.Organizer.Execution;

/// <summary>
/// Which waiting moves may run now that a storage has opened.
///
/// Relays run in rounds sized to the room in the bags, and a round only fits once the one before it has
/// drained. Opening the saddlebag used to run every move waiting for it, a later round included, while an
/// earlier round was still waiting for a retainer: the bags could then fill with stacks that had nowhere to go.
/// </summary>
public static class PendingMoveGate
{
    public static List<MoveOp> ReadyFor(IReadOnlyList<MoveOp> pending, ContainerKind opened, Func<StorageId, bool> isOpen)
    {
        // Moves within the bags and armoury never wait for a storage to open, so they cannot hold a round back.
        var waiting = pending.Where(m => m.RequiresOpen is not null).ToList();
        if (waiting.Count == 0) return new List<MoveOp>();
        var earliest = waiting.Min(m => m.Pass);
        return waiting.Where(m => m.Pass == earliest && m.RequiresOpen is { } s && s.Kind == opened && isOpen(s)).ToList();
    }

    /// <summary>The same move, whichever solve produced it: every solve hands out fresh move ids.</summary>
    public static bool SameMove(MoveOp a, MoveOp b) =>
        a.Item.Slot == b.Item.Slot && a.Item.ItemId == b.Item.ItemId && a.Item.Quantity == b.Item.Quantity && a.To == b.To && a.Leg == b.Leg;

    /// <summary>
    /// Waiting moves belong to the plan they came from. Once the layout, a rule or the storage has changed, only
    /// the ones the fresh solve still wants are kept: the rest would carry out a layout the player has since
    /// rewritten, the next time some unrelated saddlebag or retainer opened. A dropped relay is forgotten too.
    /// </summary>
    /// <returns>The moves dropped.</returns>
    public static List<MoveOp> DropStale(List<MoveOp> pending, IReadOnlyList<MoveOp> current, RelayLedger relays)
    {
        var stale = pending.Where(p => !current.Any(m => SameMove(m, p))).ToList();
        foreach (var move in stale)
        {
            pending.Remove(move);
            relays.Forget(move.MoveId);
        }
        return stale;
    }

    /// <summary>Adds the moves a run left waiting, never the same move twice.</summary>
    public static void AddWaiting(List<MoveOp> pending, IEnumerable<MoveOp> waiting)
    {
        foreach (var move in waiting)
            if (!pending.Any(p => SameMove(p, move))) pending.Add(move);
    }
}
