using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using TidyUp.Core.Lists;

namespace TidyUp.Game;

/// <summary>
/// Adds a "Gleam" submenu to the game's inventory item context menu. It says what Gleam thinks of the item
/// and offers the two decisions worth making from here: keep it always, or treat it as junk always.
/// </summary>
public sealed class ContextMenuIntegration : IDisposable
{
    private readonly IContextMenu contextMenu;
    private readonly IPlayerState player;
    private readonly IChatGui chat;
    private readonly Configuration config;
    private readonly ItemDatabase db;
    private readonly Action save;

    /// <summary>Set by the plugin: opens the window on the page the player uses.</summary>
    public Action? OpenWindow { get; set; }

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
        var info = db.Get(baseId);
        var name = info?.Name ?? $"item {baseId}";
        var isProtected = config.ProtectList.Contains(baseId, item.IsHq, cid);
        var isAlways = config.AlwaysDiscardList.Contains(baseId, item.IsHq, cid);

        // Some things the game itself refuses to part with. Saying so beats offering a choice that does nothing.
        var untouchable = info is null || info.IsIndisposable
                          || db.Curated.ProtectedItemIds.Contains(baseId)
                          || Core.Lists.HardBlocks.IsUltimateWeapon(info);

        var entries = new List<IMenuItem>();
        if (untouchable)
        {
            entries.Add(new MenuItem { Name = "Gleam never touches this", IsEnabled = false, PrefixChar = 'G' });
        }
        else
        {
            // The state first, so the menu reads as a status as much as a set of choices.
            entries.Add(new MenuItem
            {
                Name = isProtected ? "Now: kept, always" : isAlways ? "Now: junk, always" : "Now: Gleam decides",
                IsEnabled = false,
                PrefixChar = 'G',
            });
            entries.Add(new MenuItem
            {
                Name = isProtected ? "Stop keeping it" : "Keep it, always",
                PrefixChar = 'G',
                OnClicked = _ =>
                {
                    if (isProtected) config.ProtectList.RemoveAll(baseId);
                    else { config.ProtectList.Add(baseId); config.AlwaysDiscardList.RemoveAll(baseId); }
                    save();
                    chat.Print(isProtected ? $"{name} is back to normal. Gleam decides." : $"{name} will never be listed.", "Gleam");
                },
            });
            entries.Add(new MenuItem
            {
                Name = isAlways ? "Stop treating it as junk" : "Treat it as junk, always",
                PrefixChar = 'G',
                OnClicked = _ =>
                {
                    if (isAlways) config.AlwaysDiscardList.RemoveAll(baseId);
                    else { config.AlwaysDiscardList.Add(baseId); config.ProtectList.RemoveAll(baseId); }
                    save();
                    chat.Print(isAlways ? $"{name} is back to normal. Gleam decides." : $"{name} will be listed every time.", "Gleam");
                },
            });
        }
        if (OpenWindow is not null)
            entries.Add(new MenuItem { Name = "Open Gleam", PrefixChar = 'G', OnClicked = _ => OpenWindow() });

        args.AddMenuItem(new MenuItem
        {
            Name = "Gleam",
            PrefixChar = 'G',
            PrefixColor = 539,
            IsSubmenu = true,
            OnClicked = clicked => clicked.OpenSubmenu(entries),
        });
    }
}
