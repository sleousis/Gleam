using Dalamud.Game.ClientState.Conditions;
using Gleam.Core.Model;
using Gleam.Core.Stats;
using Gleam.Core.Organizer.Capacity;
using Gleam.Core.Organizer.Solving;
using Gleam.Services;

namespace Gleam.Automation;

/// <summary>
/// Hands-free organizing: walks the solver's move list in order, opening whatever storage each stretch of
/// moves needs (saddlebag here, retainers at an inn bell) and running the moves while it is open.
/// </summary>
public sealed partial class AutoPilot
{
    /// <summary>Set by the plugin.</summary>
    public OrganizerCoordinator? Organizer { get; set; }

    private int movesDone;
    private int movesPending;
    private int movesSkipped;
    private int movesFailed;

    public async Task RunOrganizerAsync()
    {
        if (IsRunning || Organizer?.Current is null) return;
        if (MissingDependency() is { } missing) { Fail(missing); return; }
        if (condition[ConditionFlag.InCombat] || condition[ConditionFlag.BoundByDuty]) { Fail("not while in combat or in a duty"); return; }
        var result = Organizer.Current;
        if (!result.Report.Feasible) { Fail("something would overflow. Change a rule or free some space first"); return; }
        if (result.Moves.Count == 0) { Nothing("Nothing to move"); return; }

        Mode = PilotMode.Organize;
        PlannedTotal = result.Moves.Count;
        IsRunning = true;
        LastError = null;
        cts?.Dispose();
        cts = new CancellationTokenSource();
        var ct = cts.Token;
        movesDone = 0;
        movesPending = 0;
        movesSkipped = 0;
        movesFailed = 0;
        tally.Clear();
        var stopped = false;
        BeginTrip();
        try
        {
            Status = "Closing leftover windows";
            await RecoverUiAsync(ct, atStart: true).ConfigureAwait(false);

            // One round at a time: run the first round, look again, and the fresh plan's first round is the next one.
            for (var round = 1; round <= 20; round++)
            {
                var moves = result.Moves.Where(m => m.Pass == 1).ToList();
                if (moves.Count == 0) break;
                await RunSessionsAsync(moves, ct).ConfigureAwait(false);
                if (result.Passes <= 1) break;

                Status = "Looking again before the next round";
                await RecoverUiAsync(ct).ConfigureAwait(false);
                await Organizer.PreviewAsync().ConfigureAwait(false);
                result = Organizer.Current ?? result;
                if (!result.Report.Feasible || result.Moves.Count == 0) break;
                PlannedTotal = PlannedDone + result.Moves.Count;
            }

            Status = "Done";
            // Failures are part of the summary. Half the moves failing used to read as "12 moved."
            var summary = $"{movesDone} moved" + (movesFailed > 0 ? $", {movesFailed} failed" : string.Empty)
                          + (movesSkipped > 0 ? $", {movesSkipped} had moved and were left alone" : string.Empty)
                          + (movesPending > 0 ? $", {movesPending} still waiting" : string.Empty)
                          + (tally.LegFailures.Count > 0 ? $", {tally.LegFailures.Count} step{(tally.LegFailures.Count == 1 ? "" : "s")} could not finish" : string.Empty);
            log.Information("Hands-free organize finished: {Summary}", summary);
            chat.Print($"Hands-free organize finished: {summary}.", "Gleam");
            foreach (var line in tally.LegFailures) chat.PrintError(line, "Gleam");
        }
        catch (OperationCanceledException)
        {
            stopped = true;
            Status = "Stopped";
            chat.Print("Stopped. Nothing else was moved.", "Gleam");
        }
        catch (AutoPilotException ex)
        {
            Fail(ex.Message);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Organizer pilot crashed");
            Fail(ex.Message);
        }
        finally
        {
            await RecoverUiAsync(CancellationToken.None).ConfigureAwait(false);
            EndTrip(RunTrigger.HandsFreeOrganize, PlannedTotal, movesDone, movesSkipped, movesFailed, movesPending, stopped, tally.LegFailures.Concat(tally.Reasons));
            IsRunning = false;
            _ = Organizer.PreviewAsync();
        }
    }

    /// <summary>Consecutive moves that need the same storage open form one session; sessions run in solver order.</summary>
    private async Task RunSessionsAsync(List<MoveOp> moves, CancellationToken ct)
    {
        var sessions = new List<(StorageId? Storage, List<MoveOp> Ops)>();
        foreach (var m in moves)
        {
            if (sessions.Count > 0 && sessions[^1].Storage == m.RequiresOpen) sessions[^1].Ops.Add(m);
            else sessions.Add((m.RequiresOpen, new List<MoveOp> { m }));
        }

        var atBell = false;
        foreach (var (storage, ops) in sessions)
        {
            ct.ThrowIfCancellationRequested();
            // Anything not at a retainer needs the bell session over first: the game refuses commands and hides
            // item-menu entries while a retainer is summoned.
            if ((storage is null || storage.Value.Kind != ContainerKind.Retainer) && atBell)
            {
                await LeaveBellAsync(ct).ConfigureAwait(false);
                atBell = false;
            }

            if (storage is null)
            {
                await Leg("bags", () => Step("Moving items within your bags and armoury chest", () => ExecuteMoves(ops), ct), ct).ConfigureAwait(false);
                continue;
            }

            if (storage.Value.Kind == ContainerKind.Saddlebag)
            {
                if (!S.OpenSaddlebag) { movesPending += ops.Count; continue; }
                await Leg("saddlebag", async () =>
                {
                    await OpenSaddlebagAsync(ct).ConfigureAwait(false);
                    await Step("Moving items to and from the saddlebag", () => ExecuteMoves(ops), ct).ConfigureAwait(false);
                    await framework.RunOnFrameworkThread(() => GameUi.Close("InventoryBuddy")).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
                continue;
            }

            if (storage.Value.Kind == ContainerKind.Retainer)
            {
                if (!S.VisitRetainers) { movesPending += ops.Count; continue; }
                var id = storage.Value.OwnerId;
                var name = Organizer?.RetainerNames.TryGetValue(id, out var n) == true ? n : "the retainer";
                if (!atBell)
                {
                    var inInn = await Leg("inn", () => TravelToInnAsync(ct), ct).ConfigureAwait(false);
                    if (!inInn) { movesPending += ops.Count; continue; }
                    await EnsureRetainerListAsync(ct).ConfigureAwait(false);
                    await WaitForMenu(RetainerListReady, StepTimeout, "the retainer list to fill", ct).ConfigureAwait(false);
                    await Task.Delay(1200, ct).ConfigureAwait(false);
                    atBell = true;
                }
                var order = await OnFramework(RetainerOrder).ConfigureAwait(false);
                var index = order.FindIndex(r => r.Id == id);
                if (index < 0)
                {
                    tally.LegFailures.Add($"{name}: not in the retainer list");
                    movesPending += ops.Count;
                    continue;
                }
                var ok = await Leg($"retainer {name}", async () =>
                {
                    await SummonRetainerAsync(index, name, ct).ConfigureAwait(false);
                    await OpenRetainerInventoryAsync(id, name, ct).ConfigureAwait(false);
                    await Step($"Moving items with {name}", () => ExecuteMoves(ops), ct).ConfigureAwait(false);
                    await CloseRetainerInventoryAsync(name, ct).ConfigureAwait(false);
                    await LeaveRetainerAsync(name, ct).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
                if (!ok) await EnsureRetainerListAsync(ct).ConfigureAwait(false);
            }
        }

        if (atBell) await LeaveBellAsync(ct).ConfigureAwait(false);
    }

    private async Task ExecuteMoves(IReadOnlyList<MoveOp> ops)
    {
        if (Organizer is null) return;
        var before = Organizer.LastReport;
        await Organizer.RunMovesAsync(ops, refreshAfter: false, RunTrigger.PartOfTrip).ConfigureAwait(false);
        var report = Organizer.LastReport;
        // A run that returned early leaves the previous report in place; counting it again doubled the tally.
        if (report is null || ReferenceEquals(report, before)) return;
        movesDone += report.Done;
        movesSkipped += report.Skipped;
        movesFailed += report.Failed;
        tally.Reasons.AddRange(report.Results.Where(r => r.Status == Core.Execution.StepStatus.Failed).Select(r => r.Message));
        movesPending += report.Pending.Count;
    }
}
