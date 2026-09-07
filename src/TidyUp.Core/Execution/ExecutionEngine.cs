using TidyUp.Core.Logging;
using TidyUp.Core.Model;

namespace TidyUp.Core.Execution;

public sealed class ExecutionOptions
{
    public TimeSpan RateLimit { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// A single failed item is skipped and reported; this many failures *in a row* mean something systemic
    /// is wrong (a dialog we cannot answer, a window that closed) and the run stops.
    /// </summary>
    public int MaxConsecutiveFailures { get; init; } = 3;

    /// <summary>What to do with a slotted item whose materia could not be retrieved.</summary>
    public MateriaFailurePolicy OnMateriaFailure { get; init; } = MateriaFailurePolicy.LeaveItem;
}

public enum MateriaFailurePolicy
{
    /// <summary>Park the item with a reason; the player can strip the materia by hand.</summary>
    LeaveItem,
    /// <summary>Act anyway. The materia is destroyed with the item.</summary>
    ActAnyway,
}

/// <summary>
/// Runs a queue against the game, one open container at a time, re-validating each slot right before acting.
/// Anything whose container is closed stays pending for a later resume; anything that changed is skipped;
/// a single failure is skipped and several in a row abort the rest.
/// </summary>
public sealed class ExecutionEngine
{
    private readonly IGameActions game;
    private readonly IRunLog log;
    private readonly IDelay delay;
    private readonly ExecutionOptions options;

    public ExecutionEngine(IGameActions game, IRunLog log, IDelay? delay = null, ExecutionOptions? options = null)
    {
        this.game = game;
        this.log = log;
        this.delay = delay ?? new RealDelay();
        this.options = options ?? new ExecutionOptions();
    }

    public async Task<RunReport> ExecuteAsync(
        IReadOnlyList<QueuedAction> queue,
        RunIdentity identity,
        CancellationToken ct,
        IProgress<ActionResult>? progress = null)
    {
        var report = new RunReport();
        touched.Clear();

        // Inventory first, dresser last: dresser restores need the slots the earlier steps free.
        var ordered = queue
            .OrderBy(q => q.Kind.ExecutionOrder())
            .ThenBy(q => q.Slot.OwnerId)
            .ThenBy(q => q.Slot.ContainerId)
            .ThenBy(q => q.Slot.Slot)
            .ToList();

        var first = true;
        var consecutiveFailures = 0;
        foreach (var action in ordered)
        {
            if (ct.IsCancellationRequested)
            {
                Park(report, action, "the run was stopped before reaching it");
                continue;
            }

            if (report.Aborted)
            {
                Park(report, action, "the run stopped at an earlier failure");
                continue;
            }

            if (!game.IsContainerAvailable(action.Kind, action.Slot.OwnerId))
            {
                Park(report, action, action.Kind.RequirementText());
                Emit(report, progress, new ActionResult(action, ActionOutcome.Pending, $"Needs: {action.Kind.RequirementText()}"));
                continue;
            }

            if (action.Action != ActionKind.Discard && !game.IsActionAvailable(action.Action))
            {
                Park(report, action, game.ActionRequirement(action.Action));
                Emit(report, progress, new ActionResult(action, ActionOutcome.Pending, $"Needs: {game.ActionRequirement(action.Action)}"));
                continue;
            }

            if (!first)
            {
                try { await delay.Wait(options.RateLimit, ct).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    Park(report, action, "the run was stopped before reaching it");
                    report.Aborted = true;
                    report.AbortReason = "cancelled";
                    continue;
                }
            }
            first = false;

            ActionResult result;
            try
            {
                result = await ExecuteOneAsync(action, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = new ActionResult(action, ActionOutcome.Cancelled, "Cancelled");
            }
            catch (Exception ex)
            {
                result = new ActionResult(action, ActionOutcome.Failed, ex.Message);
            }

            if (result.Outcome == ActionOutcome.Done)
                await log.AppendAsync(RunLogEntry.From(action, identity, result.Outcome)).ConfigureAwait(false);

            if (result.Outcome == ActionOutcome.Moved && result.Followup is { } follow)
                report.Moved.Add(follow);

            // A pre-condition discovered mid-action (no free slot, etc.) parks the item rather than failing the run.
            if (result.Outcome == ActionOutcome.Pending)
                Park(report, action, result.Message);

            Emit(report, progress, result);

            if (result.Outcome == ActionOutcome.Failed)
            {
                consecutiveFailures++;
                if (consecutiveFailures >= options.MaxConsecutiveFailures)
                {
                    report.Aborted = true;
                    report.AbortReason = $"{consecutiveFailures} items failed in a row, last: {action.ItemName}: {result.Message}";
                }
            }
            else if (result.Outcome == ActionOutcome.Done)
            {
                consecutiveFailures = 0;
            }
            if (result.Outcome == ActionOutcome.Cancelled)
            {
                report.Aborted = true;
                report.AbortReason = "cancelled";
            }
        }

        return report;
    }

    private readonly HashSet<SlotRef> touched = new();

    private async Task<ActionResult> ExecuteOneAsync(QueuedAction action, CancellationToken ct)
    {
        // Re-validate: the window is a contract. Only exactly what was shown gets touched. The *position*
        // may have come from a cache with a different slot numbering, so when the planned slot does not
        // hold the planned item, look for that exact item elsewhere in the same container.
        var target = action.Slot;
        var live = game.ReadSlot(target);
        var matches = live is not null && live.ItemId == action.ItemId && live.Quantity == action.Quantity && live.IsHq == action.IsHq;
        if (!matches || touched.Contains(target))
        {
            var found = game.FindSlot(action.Kind, action.Slot.OwnerId, action.ItemId, action.Quantity, action.IsHq, touched, action.Slot);
            if (found is null)
            {
                return live is null
                    ? new ActionResult(action, ActionOutcome.SkippedChanged, "Not found in the container any more")
                    : new ActionResult(action, ActionOutcome.SkippedChanged,
                        $"planned slot holds item {live.ItemId} ×{live.Quantity}{(live.IsHq ? " HQ" : "")} and no other slot holds item {action.ItemId} ×{action.Quantity}{(action.IsHq ? " HQ" : "")}");
            }
            target = found.Value;
            live = game.ReadSlot(target);
            if (live is null || live.ItemId != action.ItemId || live.Quantity != action.Quantity || live.IsHq != action.IsHq)
                return new ActionResult(action, ActionOutcome.SkippedChanged, "Item moved while it was being located");
        }
        touched.Add(target);

        if (action.Kind == ContainerKind.GlamourDresser)
        {
            if (game.FreeInventorySlots() < 1)
                return new ActionResult(action, ActionOutcome.Pending, "No free inventory slot to restore into");
            var restored = await game.RestoreFromDresserAsync(target, action.ItemId, ct).ConfigureAwait(false);
            if (restored is null)
                return new ActionResult(action, ActionOutcome.Failed, "Restore from dresser failed");
            target = restored.Value;
            await delay.Wait(options.RateLimit, ct).ConfigureAwait(false);
        }

        // Materia is never destroyed silently: the live item decides, not what the plan remembered.
        if (live.HasMateria && !game.CanRetrieveMateriaIn(action.Kind) && options.OnMateriaFailure == MateriaFailurePolicy.LeaveItem)
        {
            // Retainer menus have no "Retrieve Materia": bring the item home and finish there.
            if (game.FreeInventorySlots() < 1)
                return new ActionResult(action, ActionOutcome.Pending, "No free inventory slot to bring the item back for materia retrieval");
            var landed = await game.MoveToInventoryAsync(target, action.ItemId, action.Quantity, action.IsHq, ct).ConfigureAwait(false);
            if (landed is null)
                return new ActionResult(action, ActionOutcome.Pending, $"materia cannot be retrieved here and the item could not be brought back ({game.LastFailure ?? "no reason given"})");
            return new ActionResult(action, ActionOutcome.Moved, "brought back to your bags; its materia comes off there once the retainer is closed")
            {
                Followup = action with { Slot = landed.Value, RetrieveMateriaFirst = true },
            };
        }

        if (live.HasMateria && game.CanRetrieveMateriaIn(action.Kind))
        {
            if (game.FreeInventorySlots() < live.MateriaCount)
                return new ActionResult(action, ActionOutcome.Pending, "Not enough free inventory slots to retrieve materia");
            var ok = await game.RetrieveMateriaAsync(target, action.ItemId, ct).ConfigureAwait(false);
            if (!ok)
            {
                var why = game.LastFailure ?? "no reason given";
                if (options.OnMateriaFailure == MateriaFailurePolicy.LeaveItem)
                    return new ActionResult(action, ActionOutcome.Pending, $"materia could not be retrieved ({why}); remove it by hand or allow acting anyway in settings");
            }
            else
            {
                await delay.Wait(options.RateLimit, ct).ConfigureAwait(false);
            }
        }

        var success = action.Action switch
        {
            ActionKind.Discard => await game.DiscardAsync(target, action.ItemId, ct).ConfigureAwait(false),
            ActionKind.VendorSell => await game.VendorSellAsync(target, action.ItemId, ct).ConfigureAwait(false),
            ActionKind.ExpertDelivery => await game.ExpertDeliveryAsync(target, action.ItemId, ct).ConfigureAwait(false),
            ActionKind.Desynth => await game.DesynthAsync(target, action.ItemId, ct).ConfigureAwait(false),
            _ => false,
        };

        return success
            ? new ActionResult(action, ActionOutcome.Done, action.Action.Label())
            : new ActionResult(action, ActionOutcome.Failed, $"{action.Action.Label()} did not complete{(game.LastFailure is { } reason ? $": {reason}" : string.Empty)}");
    }

    private static void Park(RunReport report, QueuedAction action, string reason)
    {
        report.Pending.Add(action);
        report.PendingReasons[action] = reason;
    }

    private static void Emit(RunReport report, IProgress<ActionResult>? progress, ActionResult result)
    {
        if (result.IsTerminal) report.Results.Add(result);
        progress?.Report(result);
    }
}
