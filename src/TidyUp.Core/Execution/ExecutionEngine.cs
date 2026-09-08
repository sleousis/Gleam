using TidyUp.Core.Logging;
using TidyUp.Core.Model;
using TidyUp.Core.Rules;

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

        var core = new ExecutionCore(delay, options.RateLimit, options.MaxConsecutiveFailures);
        ActionResult? last = null;

        var summary = await core.RunAsync(ordered, new ExecutionCore.Hooks<QueuedAction>
        {
            BlockedReason = action =>
            {
                if (!game.IsContainerAvailable(action.Kind, action.Slot.OwnerId)) return action.Kind.RequirementText();
                // A retainer item that must be turned in only needs the retainer open here; the NPC comes later.
                var tripHome = ContainerConstraints.NeedsTripHome(action.Kind, action.Action);
                if (action.Action != ActionKind.Discard && !tripHome && !game.IsActionAvailable(action.Action)) return game.ActionRequirement(action.Action);
                return null;
            },
            Execute = async (action, token) =>
            {
                var result = await ExecuteOneAsync(action, token).ConfigureAwait(false);
                last = result;
                if (result.Outcome == ActionOutcome.Done)
                    await log.AppendAsync(RunLogEntry.From(action, identity, result.Outcome)).ConfigureAwait(false);
                if (result.Outcome == ActionOutcome.Moved && result.Followup is { } follow)
                    report.Moved.Add(follow);
                return new StepOutcome(ToStep(result.Outcome), result.Message);
            },
            Park = (action, reason) => Park(report, action, reason),
            Report = (action, outcome) =>
            {
                // Reuse the rich result when this outcome came from ExecuteOneAsync; synthesise one otherwise.
                var result = last is not null && ReferenceEquals(last.Action, action) && ToStep(last.Outcome) == outcome.Status && last.Message == outcome.Message
                    ? last
                    : new ActionResult(action, FromStep(outcome.Status), outcome.Message);
                Emit(report, progress, result);
            },
            Describe = action => action.ItemName,
        }, ct).ConfigureAwait(false);

        report.Aborted = summary.Aborted;
        report.AbortReason = summary.AbortReason;
        return report;
    }

    private static StepStatus ToStep(ActionOutcome o) => o switch
    {
        ActionOutcome.Done => StepStatus.Done,
        ActionOutcome.Pending => StepStatus.Pending,
        ActionOutcome.SkippedChanged => StepStatus.SkippedChanged,
        ActionOutcome.Failed => StepStatus.Failed,
        ActionOutcome.Cancelled => StepStatus.Cancelled,
        ActionOutcome.Moved => StepStatus.Moved,
        _ => StepStatus.Failed,
    };

    private static ActionOutcome FromStep(StepStatus s) => s switch
    {
        StepStatus.Done => ActionOutcome.Done,
        StepStatus.Pending => ActionOutcome.Pending,
        StepStatus.SkippedChanged => ActionOutcome.SkippedChanged,
        StepStatus.Failed => ActionOutcome.Failed,
        StepStatus.Cancelled => ActionOutcome.Cancelled,
        StepStatus.Moved => ActionOutcome.Moved,
        _ => ActionOutcome.Failed,
    };

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
                Followup = action with { Slot = landed.Value, RetrieveMateriaFirst = true, BroughtHome = true },
            };
        }

        if (ContainerConstraints.NeedsTripHome(action.Kind, action.Action))
        {
            if (game.FreeInventorySlots() < 1)
                return new ActionResult(action, ActionOutcome.Pending, $"No free inventory slot to bring the item back to {action.Action.Label()} it");
            var home = await game.MoveToInventoryAsync(target, action.ItemId, action.Quantity, action.IsHq, ct).ConfigureAwait(false);
            if (home is null)
                return new ActionResult(action, ActionOutcome.Pending, $"could not be brought back from the retainer ({game.LastFailure ?? "no reason given"})");
            return new ActionResult(action, ActionOutcome.Moved, $"brought back to your bags to {action.Action.Label()} later")
            {
                Followup = action with { Slot = home.Value, BroughtHome = true },
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

        if (action.Action == ActionKind.MarketList)
        {
            if (action.UnitPrice <= 0)
                return new ActionResult(action, ActionOutcome.Pending, "no market price is known for it; rescan with market prices on");
            if (game.FreeMarketSlots() <= 0)
                return new ActionResult(action, ActionOutcome.Pending, "this retainer's market slots are full; another retainer can take it");
        }

        var success = action.Action switch
        {
            ActionKind.Discard => await game.DiscardAsync(target, action.ItemId, ct).ConfigureAwait(false),
            ActionKind.MarketList => await game.MarketListAsync(target, action.ItemId, action.UnitPrice, action.Quantity, ct).ConfigureAwait(false),
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
