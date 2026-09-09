using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using TidyUp.Core.Lists;

namespace TidyUp.Game;

/// <summary>Adds a "Gleam" submenu to the game's inventory item context menu: never / always discard.</summary>
public sealed class ContextMenuIntegration : IDisposable
{
    private readonly IContextMenu contextMenu;
    private readonly IPlayerState player;
    private readonly IChatGui chat;
    private readonly Configuration config;
    private readonly ItemDatabase db;
    private readonly Action save;

    public ContextMenuIntegration(IContextMenu contextMenu, IPlayerState player, IChatGui chat, Configuration config, ItemDatabase db, Action save)
    {
        this.contextMenu = contextMenu;
        this.player = player;
        this.chat = chat;
        this.config = config;
        this.db = db;
        this.save = save;
        contextMenu.OnMenuOpened += OnMenuOpened;
    }

    public void Dispose() => contextMenu.OnMenuOpened -= OnMenuOpened;

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.MenuType != ContextMenuType.Inventory) return;
        if (args.Target is not MenuTargetInventory inv || inv.TargetItem is not { } item || item.ItemId == 0) return;

        var baseId = item.BaseItemId;
        var cid = player.ContentId;
        var name = db.Get(baseId)?.Name ?? $"item {baseId}";
        var isProtected = config.ProtectList.Contains(baseId, item.IsHq, cid);
        var isAlways = config.AlwaysDiscardList.Contains(baseId, item.IsHq, cid);

        args.AddMenuItem(new MenuItem
        {
            Name = "Gleam",
            PrefixChar = 'T',
            PrefixColor = 539,
            IsSubmenu = true,
            OnClicked = clicked => clicked.OpenSubmenu(new List<IMenuItem>
            {
                new MenuItem
                {
                    Name = isProtected ? "Gleam: allow again" : "Gleam: never touch",
                    PrefixChar = 'T',
                    OnClicked = _ =>
                    {
                        if (isProtected) config.ProtectList.RemoveAll(baseId);
                        else { config.ProtectList.Add(baseId); config.AlwaysDiscardList.RemoveAll(baseId); }
                        save();
                        chat.Print(isProtected ? $"{name} can be cleaned again." : $"{name} will never be touched.", "Gleam");
                    },
                },
                new MenuItem
                {
                    Name = isAlways ? "Gleam: stop always cleaning" : "Gleam: always clean",
                    PrefixChar = 'T',
                    OnClicked = _ =>
                    {
                        if (isAlways) config.AlwaysDiscardList.RemoveAll(baseId);
                        else { config.AlwaysDiscardList.Add(baseId); config.ProtectList.RemoveAll(baseId); }
                        save();
                        chat.Print(isAlways ? $"{name} is no longer always cleaned." : $"{name} will always be cleaned.", "Gleam");
                    },
                },
            }),
        });
    }
}
