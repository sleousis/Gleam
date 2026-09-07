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
    string RuleId)
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
        row.Proposal.RuleId);

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
}

public sealed record ActionResult(QueuedAction Action, ActionOutcome Outcome, string Message)
{
    public bool IsTerminal => Outcome is not ActionOutcome.Pending;
}

public sealed class RunReport
{
    public List<ActionResult> Results { get; } = new();
    public List<QueuedAction> Pending { get; } = new();
    public bool Aborted { get; set; }
    public string AbortReason { get; set; } = string.Empty;

    public int Done => Results.Count(r => r.Outcome == ActionOutcome.Done);
    public int Skipped => Results.Count(r => r.Outcome == ActionOutcome.SkippedChanged);
    public int Failed => Results.Count(r => r.Outcome == ActionOutcome.Failed);

    public Dictionary<ContainerKind, int> PendingByContainer =>
        Pending.GroupBy(p => p.Kind).ToDictionary(g => g.Key, g => g.Count());

    public string Summary()
    {
        var parts = new List<string> { $"{Done} done" };
        if (Skipped > 0) parts.Add($"{Skipped} skipped (changed)");
        if (Failed > 0) parts.Add($"{Failed} failed");
        if (Pending.Count > 0) parts.Add($"{Pending.Count} pending");
        if (Aborted) parts.Add($"aborted: {AbortReason}");
        return string.Join(", ", parts);
    }
}
