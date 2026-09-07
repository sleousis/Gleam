using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TidyUp.Game;

namespace TidyUp.Automation;

/// <summary>Small, framework-thread-only helpers for driving the game's own menus the way a click would.</summary>
public static unsafe class GameUi
{
    public static bool IsVisible(string addon) => AddonDriver.IsAddonVisible(addon);

    public static bool AnyVisible(params string[] addons) => addons.Any(IsVisible);

    public static bool Close(string addon)
    {
        var a = AddonDriver.GetAddon(addon);
        if (a == null || !a->IsVisible) return false;
        a->Close(true);
        return true;
    }

    public static void ExecuteMainCommand(uint id) => UIModule.Instance()->ExecuteMainCommand(id);

    public static bool Interact(IGameObject obj)
    {
        var target = TargetSystem.Instance();
        if (target == null || obj.Address == 0) return false;
        target->InteractWithObject((GameObject*)obj.Address, false);
        return true;
    }

    /// <summary>The entries of the open SelectString menu, in order.</summary>
    public static IReadOnlyList<string> SelectStringEntries()
    {
        var list = new List<string>();
        var addon = (AddonSelectString*)AddonDriver.GetAddon("SelectString");
        if (addon == null || !addon->AtkUnitBase.IsVisible) return list;
        var menu = &addon->PopupMenu.PopupMenu;
        if (menu->EntryNames == null) return list;
        for (var i = 0; i < menu->EntryCount; i++)
        {
            var name = menu->EntryNames[i];
            list.Add(name.HasValue ? name.ToString() : string.Empty);
        }
        return list;
    }

    /// <summary>Visible *and* populated: the menu appears a frame or two before its entries exist.</summary>
    public static bool SelectStringReady()
    {
        var addon = (AddonSelectString*)AddonDriver.GetAddon("SelectString");
        if (addon == null || !addon->AtkUnitBase.IsVisible || !addon->AtkUnitBase.IsReady) return false;
        var menu = &addon->PopupMenu.PopupMenu;
        return menu->EntryNames != null && menu->EntryCount > 0;
    }

    /// <summary>Picks the first SelectString entry containing the text. Returns the index chosen, or -1.</summary>
    public static int SelectStringChoose(string containing)
    {
        var entries = SelectStringEntries();
        for (var i = 0; i < entries.Count; i++)
        {
            if (!entries[i].Contains(containing, StringComparison.OrdinalIgnoreCase)) continue;
            var addon = AddonDriver.GetAddon("SelectString");
            if (addon == null) return -1;
            return addon->FireCallbackInt(i) ? i : -1;
        }
        return -1;
    }

    /// <summary>Fires an arbitrary int callback on a visible addon, e.g. to switch a tab.</summary>
    public static bool FireInts(string addonName, IReadOnlyList<int> ints)
    {
        var addon = AddonDriver.GetAddon(addonName);
        if (addon == null || !addon->IsVisible || ints.Count == 0) return false;
        var values = stackalloc AtkValue[ints.Count];
        for (var i = 0; i < ints.Count; i++) values[i].SetInt(ints[i]);
        return addon->FireCallback((uint)ints.Count, values, false);
    }

    /// <summary>Selects a retainer on the RetainerList by display index.</summary>
    public static bool RetainerListSelect(int firstValue, int index)
    {
        var addon = AddonDriver.GetAddon("RetainerList");
        if (addon == null || !addon->IsVisible) return false;
        var values = stackalloc AtkValue[2];
        values[0].SetInt(firstValue);
        values[1].SetInt(index);
        return addon->FireCallback(2, values, true);
    }
}
