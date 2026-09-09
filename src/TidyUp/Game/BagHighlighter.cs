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
    private int failures;

    // Each grid cell keeps its own strength so a tint grows in and shrinks out instead of snapping. Keyed on
    // the node, not the item, because it is the cell on screen that fades. Cells the game stops showing are
    // dropped at the end of a paint, so this never outgrows the open windows.
    private readonly Dictionary<nint, (float Strength, Vector4 Color)> strength = new();
    private readonly HashSet<nint> seen = new();
    private readonly List<nint> stale = new();
    private float phase;
    private float dt;
    private bool clearing;

    /// <summary>Gleam violet over items the review will clean.</summary>
    public static readonly Vector4 CleanTint = new(0.30f, 0.18f, 0.72f, 1f);
    /// <summary>Sea-glass teal over items the organizer will move, so the two never look alike.</summary>
    public static readonly Vector4 MoveTint = new(0.0f, 0.42f, 0.38f, 1f);

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
        // No fading on the way out: the plugin is going, so every cell goes back to the game's own colours now.
        clearing = true;
        try { Paint(null); } catch { /* the UI may already be gone */ }
        strength.Clear();
    }

    private void OnUpdate(IFramework fw)
    {
        if (failures > 5) return;
        try
        {
            var wanted = Source?.Invoke();
            var live = wanted is { Count: > 0 };
            // Nothing wanted and nothing still fading: leave the game's own colours alone entirely.
            if (!live && strength.Count == 0) return;

            dt = Math.Clamp((float)fw.UpdateDelta.TotalSeconds, 0f, 0.1f);
            // One shared breath across every tinted cell, so a bag reads as one highlighted set.
            phase = (phase + dt * 0.55f) % 1f;
            Paint(live ? wanted : null);
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
        seen.Clear();

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

        // A cell the game has stopped drawing cannot be faded out, so forget it rather than leak the entry.
        stale.Clear();
        foreach (var key in strength.Keys) if (!seen.Contains(key)) stale.Add(key);
        foreach (var key in stale) strength.Remove(key);
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

    /// <summary>
    /// Colours one cell at its current strength. The strength eases towards 1 while the cell is wanted and
    /// towards 0 once it is not, and a slow shared breath rides on top, so ticking a row lights the bag up
    /// rather than stamping it. At zero the game's own values are written back and the cell is forgotten.
    /// </summary>
    private void Tint(AtkComponentDragDrop* slot, Vector4? color)
    {
        if (slot == null) return;
        var node = slot->AtkComponentBase.OwnerNode;
        if (node == null) return;
        var key = (nint)node;
        var target = color is null ? 0f : 1f;

        var res = &node->AtkResNode;
        strength.TryGetValue(key, out var was);
        // A cell on its way out keeps the colour it had, or there would be nothing left to fade.
        var tint = color ?? was.Color;
        var s = clearing || Windows.Ui.Reduced
            ? target
            : was.Strength + (target - was.Strength) * (1f - MathF.Exp(-11f * dt));

        if (s < 0.02f && target == 0f)
        {
            if (strength.Remove(key) || color is null) Clear(res);
            return;
        }

        strength[key] = (s, tint);
        seen.Add(key);

        var breath = Windows.Ui.Reduced ? 1f : 0.88f + 0.12f * MathF.Sin(phase * MathF.Tau);
        var lit = s * breath;
        res->Color.A = (byte)Math.Clamp(255f - (255f - tint.W * 255f) * lit, 0, 255);
        res->AddRed = (short)(tint.X * 255f * lit);
        res->AddGreen = (short)(tint.Y * 255f * lit);
        res->AddBlue = (short)(tint.Z * 255f * lit);
    }

    private static void Clear(AtkResNode* res)
    {
        res->Color.A = 255;
        res->AddRed = 0;
        res->AddGreen = 0;
        res->AddBlue = 0;
    }
}
