using Gleam.Core.Logging;
using Gleam.Core.Model;
using Gleam.Core.Rules;

namespace Gleam.Core.Execution;

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

    /// <summary>Largest stack put up in one market listing; 0 lists the whole stack at once.</summary>
    public int MarketStackSize { get; init; }
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

        // What happened in the game happened, whether or not the history file could be written. A failed
        // write used to turn a finished action into a failure, which counted towards stopping the run.
        async Task Record(RunLogEntry entry)
        {
            try { await log.AppendAsync(entry).ConfigureAwait(false); }
            catch (Exception) { report.HistoryFailures++; }
        }

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
                    await Record(RunLogEntry.From(action, identity, result.Outcome)).ConfigureAwait(false);
                if (result.Outcome == ActionOutcome.Moved && result.Followup is { } follow)
                {
                    report.Moved.Add(follow);
                    // A partly listed stack: what did go up is history, the rest carries on as a smaller action.
                    if (!follow.BroughtHome && follow.Quantity < action.Quantity)
                        await Record(RunLogEntry.From(action with { Quantity = action.Quantity - follow.Quantity }, identity, ActionOutcome.Done)).ConfigureAwait(false);
                }
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
                    ? new ActionResult(action, ActionOutcome.SkippedChanged, "no longer in this container")
                    : new ActionResult(action, ActionOutcome.SkippedChanged, "something else is in its place and it is not elsewhere in this container");
            }
            target = found.Value;
            live = game.ReadSlot(target);
            if (live is null || live.ItemId != action.ItemId || live.Quantity != action.Quantity || live.IsHq != action.IsHq)
                return new ActionResult(action, ActionOutcome.SkippedChanged, "it moved while being located");
        }
        touched.Add(target);

        if (action.Kind == ContainerKind.GlamourDresser)
        {
            if (game.FreeInventorySlots() < 1)
                return new ActionResult(action, ActionOutcome.Pending, "no free bag space to restore it into");
            var restored = await game.RestoreFromDresserAsync(target, action.ItemId, ct).ConfigureAwait(false);
            if (restored is null)
                return new ActionResult(action, ActionOutcome.Failed, $"the dresser did not return it{(game.LastFailure is { } r0 ? $": {r0}" : string.Empty)}");
            target = restored.Value;
            // The item has left the dresser and Stop cannot put it back, so it is finished on its own timeouts.
            // A Stop here used to leave it in the bags and report it as left untouched.
            ct = CancellationToken.None;
            await delay.Wait(options.RateLimit, ct).ConfigureAwait(false);
        }

        // Materia is never destroyed silently: the live item decides, not what the plan remembered.
        if (live!.HasMateria && !game.CanRetrieveMateriaIn(action.Kind) && options.OnMateriaFailure == MateriaFailurePolicy.LeaveItem)
        {
            if (action.Kind != ContainerKind.Retainer)
                return new ActionResult(action, ActionOutcome.Pending, "materia cannot be removed while a retainer is summoned. It is finished once you leave the bell");

            // Retainer menus have no "Retrieve Materia": bring the item home and finish there.
            if (game.FreeInventorySlots() < 1)
                return new ActionResult(action, ActionOutcome.Pending, "no free bag space to bring it back for its materia");
            var landed = await game.MoveToInventoryAsync(target, action.ItemId, action.Quantity, action.IsHq, ct).ConfigureAwait(false);
            if (landed is null)
                return new ActionResult(action, ActionOutcome.Pending, $"materia cannot be retrieved here and the item could not be brought back ({game.LastFailure ?? "no reason given"})");
            return new ActionResult(action, ActionOutcome.Moved, "brought back to you. Its materia comes off once the retainer is closed")
            {
                Followup = action with { Slot = landed.Value, RetrieveMateriaFirst = true, BroughtHome = true },
            };
        }

        if (ContainerConstraints.NeedsTripHome(action.Kind, action.Action))
        {
            if (game.FreeInventorySlots() < 1)
                return new ActionResult(action, ActionOutcome.Pending, $"no free bag space to bring it back and {action.Action.Verb()} it");
            var home = await game.MoveToInventoryAsync(target, action.ItemId, action.Quantity, action.IsHq, ct).ConfigureAwait(false);
            if (home is null)
                return new ActionResult(action, ActionOutcome.Pending, $"could not be brought back from the retainer ({game.LastFailure ?? "no reason given"})");
            return new ActionResult(action, ActionOutcome.Moved, $"brought back to your bags to {action.Action.Verb()} later")
            {
                Followup = action with { Slot = home.Value, BroughtHome = true },
            };
        }

        if (live.HasMateria && game.CanRetrieveMateriaIn(action.Kind))
        {
            if (game.FreeInventorySlots() < live.MateriaCount)
                return new ActionResult(action, ActionOutcome.Pending, "not enough free bag space for its materia");
            var ok = await game.RetrieveMateriaAsync(target, action.ItemId, ct).ConfigureAwait(false);
            if (!ok)
            {
                var why = game.LastFailure ?? "no reason given";
                if (options.OnMateriaFailure == MateriaFailurePolicy.LeaveItem)
                    return new ActionResult(action, ActionOutcome.Pending, $"its materia could not be removed: {why}");
            }
            else
            {
                await delay.Wait(options.RateLimit, ct).ConfigureAwait(false);
            }
        }

        if (action.Action == ActionKind.MarketList)
        {
            if (action.UnitPrice <= 0)
                return new ActionResult(action, ActionOutcome.Pending, "no market price is known for it. Refresh with market prices on");
            if (game.FreeMarketSlots() <= 0)
                return new ActionResult(action, ActionOutcome.Pending, "this retainer's market slots are full. Another retainer can take it");
            return await ListAsync(action, target, ct).ConfigureAwait(false);
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
            : new ActionResult(action, ActionOutcome.Failed, $"could not {action.Action.Verb()} it{(game.LastFailure is { } reason ? $": {reason}" : string.Empty)}");
    }

    /// <summary>Lists the stack, in pieces when a stack size is set, until it is gone or the retainer runs out of market slots.</summary>
    private async Task<ActionResult> ListAsync(QueuedAction action, SlotRef target, CancellationToken ct)
    {
        var per = options.MarketStackSize > 0 ? Math.Min(options.MarketStackSize, action.Quantity) : action.Quantity;
        var remaining = action.Quantity;
        var listings = 0;
        while (remaining > 0)
        {
            if (game.FreeMarketSlots() <= 0) break;
            var qty = Math.Min(per, remaining);
            if (!await game.MarketListAsync(target, action.ItemId, action.UnitPrice, qty, ct).ConfigureAwait(false))
            {
                var why = game.LastFailure is { } r ? $": {r}" : string.Empty;
                if (listings == 0) return new ActionResult(action, ActionOutcome.Failed, $"could not list it on the market board{why}");
                return Partial(action, target, remaining, $"listed {action.Quantity - remaining} of {action.Quantity}. The rest could not be listed{why}");
            }
            remaining -= qty;
            listings++;
            if (remaining > 0) await delay.Wait(options.RateLimit, ct).ConfigureAwait(false);
        }
        if (remaining == 0)
            return new ActionResult(action, ActionOutcome.Done, listings > 1 ? $"{ActionKind.MarketList.Label()} · {listings} listings" : ActionKind.MarketList.Label());
        return Partial(action, target, remaining, $"listed {action.Quantity - remaining} of {action.Quantity}. This retainer's market slots are full");
    }

    private static ActionResult Partial(QueuedAction action, SlotRef target, int remaining, string message) =>
        new(action, ActionOutcome.Moved, message) { Followup = action with { Slot = target, Quantity = remaining } };

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
