using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TidyUp.Game;

using TidyUp.Core.Model;

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

    /// <summary>Types a text command into the game as if the player had, e.g. "/isort execute inventory".</summary>
    public static void SendCommand(string text)
    {
        var ui = UIModule.Instance();
        if (ui == null) return;
        var str = Utf8String.FromString(text);
        try { ui->ProcessChatBoxEntry(str); }
        finally { str->Dtor(true); }
    }

    /// <summary>The game's /itemsort targets for a container kind. Armoury pieces sort per slot.</summary>
    public static IEnumerable<string> SortTargets(ContainerKind kind) => kind switch
    {
        ContainerKind.Inventory => ["inventory"],
        ContainerKind.Saddlebag => ["saddlebag"],
        ContainerKind.Retainer => ["retainer"],
        ContainerKind.Armoury => ["mh", "oh", "head", "body", "hands", "legs", "feet", "ears", "neck", "wrists", "rings", "soul"],
        _ => [],
    };

    public static bool Interact(IGameObject obj)
    {
        var target = TargetSystem.Instance();
        if (target == null || obj.Address == 0) return false;
        target->InteractWithObject((GameObject*)obj.Address, false);
        return true;
    }

    /// <summary>Which menu addon is currently open, with its popup list.</summary>
    private static bool TryOpenMenu(out AtkUnitBase* addon, out PopupMenu* menu)
    {
        foreach (var name in MenuAddons)
        {
            var a = AddonDriver.GetAddon(name);
            if (a == null || !a->IsVisible) continue;
            addon = a;
            menu = name == "SelectString"
                ? &((AddonSelectString*)a)->PopupMenu.PopupMenu
                : &((AddonSelectIconString*)a)->PopupMenu.PopupMenu;
            return true;
        }
        addon = null;
        menu = null;
        return false;
    }

    /// <summary>Visible *and* populated: menus appear a frame or two before their entries exist.</summary>
    public static bool SelectStringReady()
    {
        if (!TryOpenMenu(out var addon, out var menu)) return false;
        return addon->IsReady && menu->EntryNames != null && menu->EntryCount > 0;
    }

    /// <summary>The entries of whichever list menu is open, in order.</summary>
    public static IReadOnlyList<string> SelectStringEntries()
    {
        var list = new List<string>();
        if (!TryOpenMenu(out _, out var menu)) return list;
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
        if (!TryOpenMenu(out var addon, out _)) return -1;
        var entries = SelectStringEntries();
        for (var i = 0; i < entries.Count; i++)
        {
            if (!entries[i].Contains(containing, StringComparison.OrdinalIgnoreCase)) continue;
            return addon->FireCallbackInt(i) ? i : -1;
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
