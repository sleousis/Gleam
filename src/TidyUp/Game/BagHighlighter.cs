using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TidyUp.Core.Model;

namespace TidyUp.Game;

/// <summary>
/// Tints item slots inside the game's own bag windows: what the review will clean, what the organizer
/// will move. The game lays bags out in its own display order (the sort you last applied), so raw slots
/// are mapped through the game's item-order tables before a grid cell is coloured. Repainted every frame
/// while there is anything to show, and cleared once when there is not.
/// </summary>
public sealed unsafe class BagHighlighter : IDisposable
{
    private readonly IFramework framework;
    private readonly IGameGui gui;
    private readonly IPluginLog log;
    private bool painted;
    private int failures;

    /// <summary>Warm gold over items the review will clean.</summary>
    public static readonly Vector4 CleanTint = new(0.45f, 0.30f, 0.0f, 1f);
    /// <summary>Cool blue over items the organizer will move.</summary>
    public static readonly Vector4 MoveTint = new(0.0f, 0.22f, 0.50f, 1f);

    /// <summary>Slots to tint, with their colour (RGB added to the icon, alpha applied). Read on the game thread each frame.</summary>
    public Func<IReadOnlyDictionary<SlotRef, Vector4>>? Source { get; set; }

    public BagHighlighter(IFramework framework, IGameGui gui, IPluginLog log)
    {
        this.framework = framework;
        this.gui = gui;
        this.log = log;
        framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        try { Paint(null); } catch { /* the UI may already be gone */ }
    }

    private void OnUpdate(IFramework _)
    {
        if (failures > 5) return;
        try
        {
            var wanted = Source?.Invoke();
            if (wanted is null || wanted.Count == 0)
            {
                if (painted) { Paint(null); painted = false; }
                return;
            }
            Paint(wanted);
            painted = true;
        }
        catch (Exception ex)
        {
            failures++;
            log.Warning(ex, "Bag highlighting failed; giving up after a few tries");
        }
    }

    private void Paint(IReadOnlyDictionary<SlotRef, Vector4>? wanted)
    {
        var order = ItemOrderModule.Instance();
        if (order == null) return;

        // Bags: three window layouts, each showing one or more 35-slot pages of the same four containers.
        var inventory = gui.GetAddonByName<AddonInventory>("Inventory");
        if (inventory != null && inventory->IsVisible)
            PaintGrid("InventoryGrid", order->InventorySorter, inventory->TabIndex, ContainerKind.Inventory, 0, 0, wanted);
        var large = gui.GetAddonByName<AddonInventoryLarge>("InventoryLarge");
        if (large != null && large->IsVisible)
        {
            PaintGrid("InventoryGrid0", order->InventorySorter, large->TabIndex * 2, ContainerKind.Inventory, 0, 0, wanted);
            PaintGrid("InventoryGrid1", order->InventorySorter, large->TabIndex * 2 + 1, ContainerKind.Inventory, 0, 0, wanted);
        }
        var expansion = gui.GetAddonByName<AddonInventoryExpansion>("InventoryExpansion");
        if (expansion != null && expansion->IsVisible)
            for (var page = 0; page < 4; page++)
                PaintGrid($"InventoryGrid{page}E", order->InventorySorter, page, ContainerKind.Inventory, 0, 0, wanted);

        // Retainer: pages 0..4 are item bags; the retainer id must match the one whose window is open.
        var retainerSorter = order->GetActiveRetainerSorter();
        var retainerId = order->ActiveRetainerId;
        var retainer = gui.GetAddonByName<AddonInventoryRetainer>("InventoryRetainer");
        if (retainer != null && retainer->IsVisible && retainerSorter != null && retainer->TabIndex <= 4)
            PaintGrid("RetainerGrid", retainerSorter, retainer->TabIndex, ContainerKind.Retainer, GameContainerIds.RetainerPage1, retainerId, wanted);
        var retainerLarge = gui.GetAddonByName<AddonInventoryRetainerLarge>("InventoryRetainerLarge");
        if (retainerLarge != null && retainerLarge->IsVisible && retainerSorter != null)
            for (var page = 0; page < 5; page++)
                PaintGrid($"RetainerGrid{page}", retainerSorter, page, ContainerKind.Retainer, GameContainerIds.RetainerPage1, retainerId, wanted);

        // Saddlebag: one window, both pages side by side, a tab for the premium half.
        var buddy = gui.GetAddonByName<AddonInventoryBuddy>("InventoryBuddy");
        if (buddy != null && buddy->IsVisible)
        {
            var premium = buddy->TabIndex == 1;
            var sorter = premium ? order->PremiumSaddleBagSorter : order->SaddleBagSorter;
            var firstContainer = premium ? GameContainerIds.PremiumSaddleBag1 : GameContainerIds.SaddleBag1;
            var slots = buddy->Slots;
            for (var i = 0; i < slots.Length; i++)
            {
                var page = i / 35;
                var cell = i % 35;
                var raw = RawSlot(sorter, page, cell);
                var color = raw is { } r && wanted is not null && wanted.TryGetValue(new SlotRef(ContainerKind.Saddlebag, firstContainer + (uint)r.Page, r.Slot), out var c) ? c : (Vector4?)null;
                Tint(slots[i].Value, color);
            }
        }
    }

    /// <summary>Colours one grid addon that shows the given display page of a sorted container.</summary>
    private void PaintGrid(string gridAddon, ItemOrderModuleSorter* sorter, int displayPage, ContainerKind kind, uint firstContainer, ulong owner, IReadOnlyDictionary<SlotRef, Vector4>? wanted)
    {
        var grid = gui.GetAddonByName<AddonInventoryGrid>(gridAddon);
        if (grid == null || sorter == null) return;
        var slots = grid->Slots;
        for (var cell = 0; cell < slots.Length; cell++)
        {
            var raw = RawSlot(sorter, displayPage, cell);
            var color = raw is { } r && wanted is not null && wanted.TryGetValue(new SlotRef(kind, firstContainer + (uint)r.Page, r.Slot, owner), out var c) ? c : (Vector4?)null;
            Tint(slots[cell].Value, color);
        }
    }

    /// <summary>The raw container page and slot shown at a display position, or null when the table has no such entry.</summary>
    private static (int Page, int Slot)? RawSlot(ItemOrderModuleSorter* sorter, int displayPage, int cell)
    {
        if (sorter == null || sorter->ItemsPerPage <= 0) return null;
        var index = displayPage * sorter->ItemsPerPage + cell;
        if (index < 0 || index >= sorter->Items.LongCount) return null;
        var entry = sorter->Items[index].Value;
        if (entry == null) return null;
        return (entry->Page, entry->Slot);
    }

    private static void Tint(AtkComponentDragDrop* slot, Vector4? color)
    {
        if (slot == null) return;
        var node = slot->AtkComponentBase.OwnerNode;
        if (node == null) return;
        var res = &node->AtkResNode;
        if (color is { } c)
        {
            res->Color.A = (byte)Math.Clamp(c.W * 255f, 0, 255);
            res->AddRed = (short)(c.X * 255f);
            res->AddGreen = (short)(c.Y * 255f);
            res->AddBlue = (short)(c.Z * 255f);
        }
        else
        {
            res->Color.A = 255;
            res->AddRed = 0;
            res->AddGreen = 0;
            res->AddBlue = 0;
        }
    }
}
