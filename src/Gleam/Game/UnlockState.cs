using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.Exd;

namespace Gleam.Game;

/// <summary>Whether an item that registers something (minion, mount, orchestrion roll, card, emote...) is already registered.</summary>
public static unsafe class UnlockState
{
    /// <summary>True registered, false not yet, null when the item registers nothing. Framework thread.</summary>
    public static bool? Of(uint itemId)
    {
        var row = ExdModule.GetItemRowById(itemId);
        if (row == null) return null;
        var ui = UIState.Instance();
        if (ui == null) return null;
        return ui->IsItemActionUnlocked(row) switch
        {
            1 => true,
            2 => false,
            _ => null,
        };
    }
}
