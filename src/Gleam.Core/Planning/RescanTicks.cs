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
        var was = before.AllRows.GroupBy(Identity).ToDictionary(g => g.Key, g => g.First().Checked);
        foreach (var row in fresh.AllRows)
        {
            if (was.TryGetValue(Identity(row), out var ticked)) row.Checked = ticked && row.IsExecutable;
            else if (!userAsked) row.Checked = false;
        }
    }
}
