using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using TidyUp.Core.Merging;
using TidyUp.Core.Model;

namespace TidyUp.Game;

/// <summary>Executes <see cref="MergeMove"/>s. Moving a stack onto another stack of the same item merges them in-game.</summary>
public sealed class StackMerger
{
    private readonly IFramework framework;
    private readonly IPluginLog log;

    public StackMerger(IFramework framework, IPluginLog log)
    {
        this.framework = framework;
        this.log = log;
    }

    /// <summary>Returns how many moves the game accepted.</summary>
    public async Task<int> ExecuteAsync(IReadOnlyList<MergeMove> moves, TimeSpan pause, CancellationToken ct)
    {
        var accepted = 0;
        foreach (var move in moves)
        {
            if (ct.IsCancellationRequested) break;
            if (move.From.Kind == ContainerKind.GlamourDresser || move.To.Kind == ContainerKind.GlamourDresser) continue;

            var ok = await framework.RunOnFrameworkThread(() => TryMove(move)).ConfigureAwait(false);
            if (ok) accepted++;
            else log.Debug("Merge {From} → {To} refused", move.From, move.To);
            await Task.Delay(pause, ct).ConfigureAwait(false);
        }
        return accepted;
    }

    private static unsafe bool TryMove(MergeMove move)
    {
        var im = InventoryManager.Instance();
        if (im == null) return false;
        var src = im->GetInventorySlot((InventoryType)move.From.ContainerId, move.From.Slot);
        var dst = im->GetInventorySlot((InventoryType)move.To.ContainerId, move.To.Slot);
        if (src == null || dst == null) return false;
        if (ScannedItem.BaseItemId(src->ItemId) != move.ItemId || ScannedItem.BaseItemId(dst->ItemId) != move.ItemId) return false;
        var result = im->MoveItemSlot((InventoryType)move.From.ContainerId, (ushort)move.From.Slot,
            (InventoryType)move.To.ContainerId, (ushort)move.To.Slot, true);
        return result != 0;
    }
}
