using TidyUp.Core.Model;

namespace TidyUp.Core.Merging;

/// <summary>Move <see cref="Quantity"/> units from one slot onto another stack of the same item.</summary>
public sealed record MergeMove(SlotRef From, SlotRef To, uint ItemId, int Quantity);

/// <summary>
/// Non-destructive first pass: finds split stacks of the same item (same HQ flag, no materia, not collectable)
/// within the same container kind and owner, and plans moves that consolidate them. Never crosses kinds:
/// inventory stacks stay in the inventory, retainer stacks stay with their retainer.
/// </summary>
public static class StackMergePlanner
{
    public static IReadOnlyList<MergeMove> Plan(IEnumerable<ScannedItem> items, Func<uint, ItemInfo?> infoLookup)
    {
        var moves = new List<MergeMove>();
        var groups = items
            .Where(i => !i.IsCollectable && !i.HasMateria && i.Quantity > 0)
            .GroupBy(i => (i.Slot.Kind, i.Slot.OwnerId, i.ItemId, i.IsHq));

        foreach (var g in groups)
        {
            var stacks = g.OrderByDescending(i => i.Quantity).ThenBy(i => i.Slot.ContainerId).ThenBy(i => i.Slot.Slot).ToList();
            if (stacks.Count < 2) continue;
            var info = infoLookup(g.Key.ItemId);
            if (info is null || info.StackSize <= 1) continue;
            var max = (int)info.StackSize;

            // Fill the fullest stacks first from the emptiest.
            var remaining = stacks.Select(s => s.Quantity).ToArray();
            for (var to = 0; to < stacks.Count; to++)
            {
                for (var from = stacks.Count - 1; from > to; from--)
                {
                    if (remaining[from] <= 0) continue;
                    var room = max - remaining[to];
                    if (room <= 0) break;
                    var qty = Math.Min(room, remaining[from]);
                    moves.Add(new MergeMove(stacks[from].Slot, stacks[to].Slot, g.Key.ItemId, qty));
                    remaining[to] += qty;
                    remaining[from] -= qty;
                }
            }
        }

        return moves;
    }

    /// <summary>How many slots the moves free (a source stack that reaches zero frees its slot).</summary>
    public static int SlotsFreed(IReadOnlyList<MergeMove> moves, IEnumerable<ScannedItem> items)
    {
        var remaining = items.ToDictionary(i => i.Slot, i => i.Quantity);
        foreach (var m in moves)
            if (remaining.ContainsKey(m.From)) remaining[m.From] -= m.Quantity;
        return remaining.Count(kv => kv.Value <= 0 && moves.Any(m => m.From == kv.Key));
    }
}
