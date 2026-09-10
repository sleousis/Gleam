using TidyUp.Core.Model;
using TidyUp.Core.Organizer.Capacity;
using TidyUp.Core.Organizer.Solving;

namespace TidyUp.Core.Organizer.Execution;

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
}
