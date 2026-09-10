using Dalamud.Plugin.Services;
using Gleam.Core.Merging;
using Gleam.Core.Model;
using Gleam.Core.Organizer.Execution;

namespace Gleam.Game;

/// <summary>Executes <see cref="MergeMove"/>s through the confirmed mover. Moving a stack onto another stack of the same item merges them in-game.</summary>
public sealed class StackMerger
{
    private readonly IMoveActions mover;
    private readonly IPluginLog log;

    public StackMerger(IMoveActions mover, IPluginLog log)
    {
        this.mover = mover;
        this.log = log;
    }

    /// <summary>Returns how many moves the game completed.</summary>
    public async Task<int> ExecuteAsync(IReadOnlyList<MergeMove> moves, TimeSpan pause, CancellationToken ct)
    {
        var accepted = 0;
        foreach (var move in moves)
        {
            if (ct.IsCancellationRequested) break;
            if (move.From.Kind == ContainerKind.GlamourDresser || move.To.Kind == ContainerKind.GlamourDresser) continue;

            var src = mover.ReadSlot(move.From);
            if (src is null || src.ItemId != move.ItemId) continue;
            var outcome = await mover.MoveAsync(move.From, move.To, move.ItemId, src.Quantity, ct).ConfigureAwait(false);
            if (outcome.Status == MoveStatus.Done) accepted++;
            else log.Debug("Merge {From} -> {To} {Status}: {Why}", move.From, move.To, outcome.Status, outcome.Message);
            await Task.Delay(pause, ct).ConfigureAwait(false);
        }
        return accepted;
    }
}
