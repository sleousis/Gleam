namespace TidyUp.Core.Execution;

public enum StepStatus
{
    Done,
    /// <summary>Could not run now (container closed, precondition missing); stays queued.</summary>
    Pending,
    /// <summary>The live slot no longer matches what was planned; nothing was touched.</summary>
    SkippedChanged,
    Failed,
    Cancelled,
    /// <summary>The item was relocated as a first leg; a follow-up carries on elsewhere.</summary>
    Moved,
}

public readonly record struct StepOutcome(StepStatus Status, string Message)
{
    public bool IsTerminal => Status != StepStatus.Pending;
}

/// <summary>
/// The part of running a queue against the game that is the same whatever the queue holds: skip what is
/// blocked, pace the calls, turn exceptions into outcomes, park what cannot run, and stop after several
/// failures in a row. The cleaner's engine and the organizer's mover both sit on it.
/// </summary>
public sealed class ExecutionCore
{
    private readonly IDelay delay;
    private readonly TimeSpan rateLimit;
    private readonly int maxConsecutiveFailures;

    public ExecutionCore(IDelay delay, TimeSpan rateLimit, int maxConsecutiveFailures)
    {
        this.delay = delay;
        this.rateLimit = rateLimit;
        this.maxConsecutiveFailures = maxConsecutiveFailures;
    }

    public sealed class Hooks<TOp>
    {
        /// <summary>A reason the op cannot run right now, or null. Blocked ops are parked and reported as pending.</summary>
        public required Func<TOp, string?> BlockedReason { get; init; }

        /// <summary>Runs one op. Exceptions are turned into Failed (or Cancelled) outcomes by the core.</summary>
        public required Func<TOp, CancellationToken, Task<StepOutcome>> Execute { get; init; }

        /// <summary>Records that an op stays queued, with why.</summary>
        public required Action<TOp, string> Park { get; init; }

        /// <summary>Surfaces an outcome to the report and progress listeners.</summary>
        public required Action<TOp, StepOutcome> Report { get; init; }

        /// <summary>Short name for abort messages.</summary>
        public Func<TOp, string> Describe { get; init; } = op => op?.ToString() ?? string.Empty;
    }

    public sealed record Summary(bool Aborted, string AbortReason);

    public async Task<Summary> RunAsync<TOp>(IReadOnlyList<TOp> ops, Hooks<TOp> hooks, CancellationToken ct)
    {
        var aborted = false;
        var abortReason = string.Empty;
        var first = true;
        var consecutiveFailures = 0;

        foreach (var op in ops)
        {
            if (ct.IsCancellationRequested)
            {
                hooks.Park(op, "the run was stopped before reaching it");
                continue;
            }

            if (aborted)
            {
                hooks.Park(op, "the run stopped at an earlier failure");
                continue;
            }

            if (hooks.BlockedReason(op) is { } blocked)
            {
                hooks.Park(op, blocked);
                hooks.Report(op, new StepOutcome(StepStatus.Pending, $"Needs: {blocked}"));
                continue;
            }

            if (!first)
            {
                try { await delay.Wait(rateLimit, ct).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    hooks.Park(op, "the run was stopped before reaching it");
                    aborted = true;
                    abortReason = "cancelled";
                    continue;
                }
            }
            first = false;

            StepOutcome outcome;
            try
            {
                outcome = await hooks.Execute(op, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                outcome = new StepOutcome(StepStatus.Cancelled, "Cancelled");
            }
            catch (Exception ex)
            {
                outcome = new StepOutcome(StepStatus.Failed, ex.Message);
            }

            // A pre-condition discovered mid-step (no free slot, etc.) parks the op rather than failing the run.
            if (outcome.Status == StepStatus.Pending) hooks.Park(op, outcome.Message);

            hooks.Report(op, outcome);

            switch (outcome.Status)
            {
                case StepStatus.Failed:
                    consecutiveFailures++;
                    if (consecutiveFailures >= maxConsecutiveFailures)
                    {
                        aborted = true;
                        abortReason = $"{consecutiveFailures} items failed in a row, last: {hooks.Describe(op)}: {outcome.Message}";
                    }
                    break;
                case StepStatus.Done:
                    consecutiveFailures = 0;
                    break;
                case StepStatus.Cancelled:
                    aborted = true;
                    abortReason = "cancelled";
                    break;
            }
        }

        return new Summary(aborted, abortReason);
    }
}
