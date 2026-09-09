using TidyUp.Core.Execution;
using TidyUp.Core.Logging;
using TidyUp.Core.Model;
using TidyUp.Core.Organizer.Capacity;
using TidyUp.Core.Organizer.Solving;

namespace TidyUp.Core.Organizer.Execution;

public sealed record MoveResult(MoveOp Op, StepStatus Status, string Message)
{
    public bool IsTerminal => Status != StepStatus.Pending;
}

public sealed class MoveRunReport
{
    public List<MoveResult> Results { get; } = new();
    public List<MoveOp> Pending { get; } = new();
    public Dictionary<MoveOp, string> PendingReasons { get; } = new();
    public bool Aborted { get; set; }
    public string AbortReason { get; set; } = string.Empty;

    public int Done => Results.Count(r => r.Status == StepStatus.Done);
    public int Skipped => Results.Count(r => r.Status == StepStatus.SkippedChanged);
    public int Failed => Results.Count(r => r.Status == StepStatus.Failed);

    public IEnumerable<(string Reason, int Count)> PendingByReason() =>
        Pending.GroupBy(p => PendingReasons.TryGetValue(p, out var r) ? r : string.Empty).Select(g => (g.Key, g.Count()));

    public string Summary()
    {
        var parts = new List<string> { $"{Done} moved" };
        if (Skipped > 0) parts.Add($"{Skipped} had moved and {(Skipped == 1 ? "was" : "were")} left alone");
        if (Failed > 0) parts.Add($"{Failed} failed");
        if (Pending.Count > 0) parts.Add($"{Pending.Count} waiting");
        return string.Join(", ", parts);
    }
}

public sealed class MoveExecutorOptions
{
    public TimeSpan RateLimit { get; init; } = TimeSpan.FromMilliseconds(250);
    public int MaxConsecutiveFailures { get; init; } = 3;
}

/// <summary>
/// Runs solver moves in order against whatever storages are open, re-finding each stack by identity
/// and choosing the landing slot live. Anything whose storage is closed waits; a relay's second leg
/// waits until its first leg has happened. Built on the same core loop as the cleaner.
/// </summary>
public sealed class MoveExecutor
{
    private readonly IMoveActions game;
    private readonly IMoveLog log;
    private readonly IDelay delay;
    private readonly MoveExecutorOptions options;

    public MoveExecutor(IMoveActions game, IMoveLog log, IDelay? delay = null, MoveExecutorOptions? options = null)
    {
        this.game = game;
        this.log = log;
        this.delay = delay ?? new RealDelay();
        this.options = options ?? new MoveExecutorOptions();
    }

    public async Task<MoveRunReport> ExecuteAsync(IReadOnlyList<MoveOp> ops, RunIdentity identity, CancellationToken ct, IProgress<MoveResult>? progress = null)
    {
        var report = new MoveRunReport();
        var touched = new HashSet<SlotRef>();
        var reserved = new HashSet<SlotRef>();
        var completedFirstLegs = new HashSet<Guid>();
        MoveResult? last = null;

        // Relay rounds run one at a time: a later round's moves wait until nothing from an earlier round is left.
        var currentRound = ops.Count == 0 ? 1 : ops.Min(o => o.Pass);

        var core = new ExecutionCore(delay, options.RateLimit, options.MaxConsecutiveFailures);
        var summary = await core.RunAsync(ops, new ExecutionCore.Hooks<MoveOp>
        {
            BlockedReason = op =>
            {
                if (op.Pass > currentRound) return "an earlier round has to finish first";
                if (!game.IsOpen(op.From)) return op.From.Kind.RequirementText();
                if (!game.IsOpen(op.To)) return op.To.Kind.RequirementText();
                return null;
            },
            Execute = async (op, token) =>
            {
                var result = await ExecuteOneAsync(op, touched, reserved, completedFirstLegs, token).ConfigureAwait(false);
                last = result;
                if (result.Status == StepStatus.Done)
                {
                    completedFirstLegs.Add(op.MoveId);
                    await log.AppendAsync(new MoveLogEntry(DateTimeOffset.UtcNow, identity.CharacterId, identity.CharacterName,
                        op.Item.ItemId, op.Info.Name, op.Item.Quantity, op.Item.IsHq, op.From.Kind, op.From.OwnerId, op.To.Kind, op.To.OwnerId,
                        op.Leg.ToString(), string.Empty)).ConfigureAwait(false);
                }
                return new StepOutcome(result.Status, result.Message);
            },
            Park = (op, reason) => { report.Pending.Add(op); report.PendingReasons[op] = reason; },
            Report = (op, outcome) =>
            {
                var result = last is not null && ReferenceEquals(last.Op, op) && last.Status == outcome.Status && last.Message == outcome.Message
                    ? last
                    : new MoveResult(op, outcome.Status, outcome.Message);
                if (result.IsTerminal) report.Results.Add(result);
                progress?.Report(result);
            },
            Describe = op => op.Info.Name,
        }, ct).ConfigureAwait(false);

        report.Aborted = summary.Aborted;
        report.AbortReason = summary.AbortReason;
        return report;
    }

    private async Task<MoveResult> ExecuteOneAsync(MoveOp op, HashSet<SlotRef> touched, HashSet<SlotRef> reserved, HashSet<Guid> firstLegsDone, CancellationToken ct)
    {
        // The source is found by identity: planned positions may come from a cache, and a relay's second
        // leg only knows the stack landed *somewhere* in the bags.
        var preferred = op.Leg == MoveLeg.RelayIn ? (SlotRef?)null : op.Item.Slot;
        var source = game.FindSlot(op.From, op.Item.ItemId, op.Item.Quantity, op.Item.IsHq, touched, preferred);
        if (source is null)
        {
            if (op.Leg == MoveLeg.RelayIn && !firstLegsDone.Contains(op.MoveId))
                return new MoveResult(op, StepStatus.Pending, "its first move into the bags has not happened yet");
            return new MoveResult(op, StepStatus.SkippedChanged, "the stack is no longer where it was");
        }

        var landing = game.FindLanding(op.To, op.Item.ItemId, op.Item.IsHq, op.PreferredPage, reserved);
        if (landing is null)
            return new MoveResult(op, StepStatus.Pending, $"no room left in {Describe(op.To)}");

        touched.Add(source.Value);
        var outcome = await game.MoveAsync(source.Value, landing.Value, op.Item.ItemId, op.Item.Quantity, ct).ConfigureAwait(false);
        switch (outcome.Status)
        {
            case MoveStatus.Done:
                reserved.Add(landing.Value);
                return new MoveResult(op, StepStatus.Done, $"{Describe(op.From)} → {Describe(op.To)}");
            case MoveStatus.SourceChanged:
                return new MoveResult(op, StepStatus.SkippedChanged, outcome.Message);
            default:
                return new MoveResult(op, StepStatus.Failed, outcome.Message);
        }
    }

    private static string Describe(StorageId s) => s.Kind == ContainerKind.Retainer ? "the retainer" : s.Kind.DisplayName().ToLowerInvariant();
}
