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
    /// <summary>The two list menus NPCs use: plain text (retainers, officers) and icon rows (merchants).</summary>
    private static readonly string[] MenuAddons = ["SelectString", "SelectIconString"];

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

    /// <summary>Which menu addon is currently open, or null.</summary>
    private static (AtkUnitBase* Addon, PopupMenu* Menu)? OpenMenu()
    {
        foreach (var name in MenuAddons)
        {
            var addon = AddonDriver.GetAddon(name);
            if (addon == null || !addon->IsVisible) continue;
            PopupMenu* menu = name == "SelectString"
                ? &((AddonSelectString*)addon)->PopupMenu.PopupMenu
                : &((AddonSelectIconString*)addon)->PopupMenu.PopupMenu;
            return (addon, menu);
        }
        return null;
    }

    /// <summary>Visible *and* populated: menus appear a frame or two before their entries exist.</summary>
    public static bool SelectStringReady()
    {
        var open = OpenMenu();
        if (open is null) return false;
        var (addon, menu) = open.Value;
        return addon->IsReady && menu->EntryNames != null && menu->EntryCount > 0;
    }

    /// <summary>The entries of whichever list menu is open, in order.</summary>
    public static IReadOnlyList<string> SelectStringEntries()
    {
        var list = new List<string>();
        var open = OpenMenu();
        if (open is null) return list;
        var menu = open.Value.Menu;
        if (menu->EntryNames == null) return list;
        for (var i = 0; i < menu->EntryCount; i++)
        {
            var name = menu->EntryNames[i];
            list.Add(name.HasValue ? name.ToString() : string.Empty);
        }
        return list;
    }

    /// <summary>Picks the first entry containing the text. Returns the index chosen, or -1.</summary>
    public static int SelectStringChoose(string containing)
    {
        var open = OpenMenu();
        if (open is null) return -1;
        var entries = SelectStringEntries();
        for (var i = 0; i < entries.Count; i++)
        {
            if (!entries[i].Contains(containing, StringComparison.OrdinalIgnoreCase)) continue;
            return open.Value.Addon->FireCallbackInt(i) ? i : -1;
        }
        return -1;
    }

    /// <summary>Closes whichever list menu is open.</summary>
    public static void CloseMenu()
    {
        foreach (var name in MenuAddons) Close(name);
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
