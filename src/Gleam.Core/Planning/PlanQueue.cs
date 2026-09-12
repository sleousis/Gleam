using Gleam.Core.Execution;

namespace Gleam.Core.Planning;

/// <summary>Turns rows of the list into the queue a run carries out.</summary>
public static class PlanQueue
{
    /// <summary>Executable rows that pass the filter, as a queue.</summary>
    /// <param name="requireChecked">
    /// False only for work done while the player is away, which picks its own rows (what a rule would tick
    /// on its own) rather than trusting ticks that may be the player's, made for a different moment.
    /// </param>
    public static List<QueuedAction> Build(IEnumerable<PlanRow> rows, Func<PlanRow, bool> filter, bool requireChecked = true) =>
        rows.Where(r => (!requireChecked || r.Checked) && r.IsExecutable && filter(r)).Select(QueuedAction.FromRow).ToList();
}
