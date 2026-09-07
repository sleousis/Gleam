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
}

/// <summary>
/// Runs a queue against the game, one open container at a time, re-validating each slot right before acting.
/// Anything whose container is closed stays pending for a later resume; anything that changed is skipped;
/// the first failure aborts everything that remains.
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

            if (!first) await delay.Wait(options.RateLimit, ct).ConfigureAwait(false);
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

    private async Task<ActionResult> ExecuteOneAsync(QueuedAction action, CancellationToken ct)
    {
        // Re-validate: the window is a contract. Only exactly what was shown gets touched.
        var live = game.ReadSlot(action.Slot);
        if (live is null)
            return new ActionResult(action, ActionOutcome.SkippedChanged, "Slot is now empty");
        if (live.ItemId != action.ItemId || live.Quantity != action.Quantity || live.IsHq != action.IsHq)
            return new ActionResult(action, ActionOutcome.SkippedChanged,
                $"Slot now holds {live.ItemId} ×{live.Quantity}{(live.IsHq ? " HQ" : "")}, expected {action.ItemId} ×{action.Quantity}{(action.IsHq ? " HQ" : "")}");

        var target = action.Slot;

        if (action.Kind == ContainerKind.GlamourDresser)
        {
            if (game.FreeInventorySlots() < 1)
                return new ActionResult(action, ActionOutcome.Pending, "No free inventory slot to restore into");
            var restored = await game.RestoreFromDresserAsync(action.Slot, action.ItemId, ct).ConfigureAwait(false);
            if (restored is null)
                return new ActionResult(action, ActionOutcome.Failed, "Restore from dresser failed");
            target = restored.Value;
            await delay.Wait(options.RateLimit, ct).ConfigureAwait(false);
        }

        if (action.RetrieveMateriaFirst)
        {
            if (game.FreeInventorySlots() < live.MateriaCount)
                return new ActionResult(action, ActionOutcome.Pending, "Not enough free inventory slots to retrieve materia");
            var ok = await game.RetrieveMateriaAsync(target, action.ItemId, ct).ConfigureAwait(false);
            if (!ok) return new ActionResult(action, ActionOutcome.Failed, "Materia retrieval failed");
            await delay.Wait(options.RateLimit, ct).ConfigureAwait(false);
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
            : new ActionResult(action, ActionOutcome.Failed, $"{action.Action.Label()} did not complete");
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
