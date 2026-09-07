using TidyUp.Core.Model;
using TidyUp.Core.Planning;

namespace TidyUp.Core.Execution;

/// <summary>Exactly what the confirmation window showed, frozen. Execution re-reads the slot and refuses anything that differs.</summary>
public sealed record QueuedAction(
    SlotRef Slot,
    uint ItemId,
    int Quantity,
    bool IsHq,
    ActionKind Action,
    bool RetrieveMateriaFirst,
    string ItemName,
    long ValueGil,
    string RuleId,
    long UnitPrice = 0,
    bool BroughtHome = false)
{
    public static QueuedAction FromRow(PlanRow row) => new(
        row.Item.Slot,
        row.Item.ItemId,
        row.Item.Quantity,
        row.Item.IsHq,
        row.ChosenAction,
        row.Item.HasMateria,
        row.Info.Name,
        row.Proposal.ValueGil,
        row.Proposal.RuleId,
        row.ChosenAction == ActionKind.MarketList ? row.Proposal.MarketUnitPrice : 0);

    public ContainerKind Kind => Slot.Kind;
}

public enum ActionOutcome
{
    Done,
    /// <summary>Slot no longer holds what the window showed; nothing was touched.</summary>
    SkippedChanged,
    /// <summary>Container or required NPC window is not open; still pending.</summary>
    Pending,
    /// <summary>The game refused or the action timed out. The run aborts after the first failure.</summary>
    Failed,
    Cancelled,
    /// <summary>Brought back to the player's bags so its materia can come off there; the follow-up is in <see cref="RunReport.Moved"/>.</summary>
    Moved,
}

public sealed record ActionResult(QueuedAction Action, ActionOutcome Outcome, string Message)
{
    public bool IsTerminal => Outcome is not ActionOutcome.Pending;

    /// <summary>For <see cref="ActionOutcome.Moved"/>: the same action, re-addressed to where the item landed.</summary>
    public QueuedAction? Followup { get; init; }
}

public sealed class RunReport
{
    public List<ActionResult> Results { get; } = new();
    public List<QueuedAction> Pending { get; } = new();

    /// <summary>Actions re-addressed to the player's bags after the item was pulled out of a retainer. Run them once the retainer is closed.</summary>
    public List<QueuedAction> Moved { get; } = new();

    /// <summary>Why each pending action is waiting, so the summary can say "open a vendor" rather than nothing.</summary>
    public Dictionary<QueuedAction, string> PendingReasons { get; } = new();

    public IEnumerable<(string Reason, int Count)> PendingByReason() =>
        Pending.GroupBy(p => PendingReasons.TryGetValue(p, out var r) ? r : string.Empty)
            .Select(g => (g.Key, g.Count()));
    public bool Aborted { get; set; }
    public string AbortReason { get; set; } = string.Empty;

    public int Done => Results.Count(r => r.Outcome == ActionOutcome.Done);
    public int Skipped => Results.Count(r => r.Outcome == ActionOutcome.SkippedChanged);
    public int Failed => Results.Count(r => r.Outcome == ActionOutcome.Failed);

    public Dictionary<ContainerKind, int> PendingByContainer =>
        Pending.GroupBy(p => p.Kind).ToDictionary(g => g.Key, g => g.Count());

    public string Summary()
    {
        var parts = new List<string> { $"{Done} cleaned" };
        if (Skipped > 0) parts.Add($"{Skipped} had moved and were left alone");
        if (Failed > 0) parts.Add($"{Failed} failed");
        if (Moved.Count > 0) parts.Add($"{Moved.Count} brought home for later");
        if (Pending.Count > 0) parts.Add($"{Pending.Count} waiting");
        if (Aborted) parts.Add($"stopped: {AbortReason}");
        return string.Join(", ", parts);
    }
}
