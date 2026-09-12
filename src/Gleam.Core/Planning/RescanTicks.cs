using Gleam.Core.Model;

namespace Gleam.Core.Planning;

/// <summary>
/// The player's own ticks survive a re-scan, matched by item rather than slot because a retainer's live slots
/// differ from the cached ones. An item new since the list was last looked at starts unticked when nobody asked
/// for this scan: loot landing while the window is open must never be waiting, ticked, behind a button the
/// player is about to press.
/// </summary>
public static class RescanTicks
{
    /// <summary>A row as a re-scan recognises it: container, owner, item, quality and stack size.</summary>
    public static (ContainerKind Kind, ulong OwnerId, uint ItemId, bool IsHq, int Quantity) Identity(PlanRow r) =>
        (r.Item.Slot.Kind, r.Item.Slot.OwnerId, r.Item.ItemId, r.Item.IsHq, r.Item.Quantity);

    /// <summary>Carries the ticks of <paramref name="before"/> over to the fresh plan's rows, in place.</summary>
    /// <param name="userAsked">The player pressed Look again: rows new to the list keep the planner's own tick.</param>
    public static void CarryOver(RunPlan? before, RunPlan fresh, bool userAsked)
    {
        if (before is null) return;
        var was = before.AllRows.GroupBy(Identity).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var group in fresh.AllRows.GroupBy(Identity))
        {
            if (!was.TryGetValue(group.Key, out var olds))
            {
                if (!userAsked) foreach (var row in group) row.Checked = false;
                continue;
            }
            // Identical stacks share an identity. Each keeps the tick of the copy in its own slot, then of the next
            // copy not yet matched. The first copy's tick used to go to all of them, re-ticking one the player had
            // unticked.
            var left = new List<PlanRow>(olds);
            var unmatched = new List<PlanRow>();
            foreach (var row in group)
            {
                var same = left.FirstOrDefault(o => o.Item.Slot == row.Item.Slot);
                if (same is null) { unmatched.Add(row); continue; }
                row.Checked = same.Checked && row.IsExecutable;
                left.Remove(same);
            }
            foreach (var row in unmatched)
            {
                if (left.Count > 0) { row.Checked = left[0].Checked && row.IsExecutable; left.RemoveAt(0); }
                else if (!userAsked) row.Checked = false; // one more copy than before: new to the list
            }
        }
    }

    /// <summary>
    /// Keys of rows the player unticked, moved to where the same items sit after a re-scan. A key names a slot, and
    /// sorting or a retainer's real slot numbers move stacks. Without this, an item unticked in the review was picked
    /// again by the after-ventures clean once the bags had been sorted.
    /// </summary>
    public static List<string> CarrySkips(RunPlan? before, RunPlan fresh, IReadOnlySet<string> skips)
    {
        var moved = new List<string>();
        if (before is null || skips.Count == 0) return moved;
        var skipped = before.AllRows.Where(r => skips.Contains(r.Key)).Select(Identity).ToHashSet();
        foreach (var row in fresh.AllRows)
            if (!row.Checked && skipped.Contains(Identity(row)) && !skips.Contains(row.Key)) moved.Add(row.Key);
        return moved;
    }
}
