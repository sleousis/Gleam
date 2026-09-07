using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using TidyUp.Core.Integrations;
using TidyUp.Core.Model;
using TidyUp.Game;
using TidyUp.Integrations;

namespace TidyUp.Windows;

/// <summary>Phase 0 spikes as buttons. Each does one thing to one item and prints what happened.</summary>
public sealed class DebugWindow : Window
{
    private readonly IFramework framework;
    private readonly GameActions actions;
    private readonly GameInventoryScanner scanner;
    private readonly InventoryContextDriver context;
    private readonly ItemDatabase db;
    private readonly AllaganToolsSource allagan;
    private readonly IMarketPriceSource market;
    private readonly IPlayerState player;
    private readonly Configuration config;
    private readonly List<string> lines = new();

    private int container = (int)GameContainerIds.Inventory1;
    private int slot;
    private int dresserIndex;
    private int itemId = 5;

    public DebugWindow(IFramework framework, GameActions actions, GameInventoryScanner scanner, InventoryContextDriver context,
        ItemDatabase db, AllaganToolsSource allagan, IMarketPriceSource market, IPlayerState player, Configuration config)
        : base("Tidy Up Spikes###TidyUpDebug")
    {
        this.framework = framework;
        this.actions = actions;
        this.scanner = scanner;
        this.context = context;
        this.db = db;
        this.allagan = allagan;
        this.market = market;
        this.player = player;
        this.config = config;
        Size = new Vector2(760, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    private void Log(string s)
    {
        lines.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {s}");
        if (lines.Count > 200) lines.RemoveAt(lines.Count - 1);
    }

    public override void Draw()
    {
        Ui.TextColored(Ui.Warn, "These act on real items. Point them at junk.");
        Ui.Header("Target");
        ImGui.SetNextItemWidth(120 * Ui.Scale); Ui.InputInt("Container id", ref container);
        Ui.HelpMarker("0-3 inventory pages, 3200-3500 armoury, 4000/4001/4100/4101 saddlebag, 10000-10006 retainer pages");
        ImGui.SetNextItemWidth(120 * Ui.Scale); Ui.InputInt("Slot", ref slot);
        ImGui.SetNextItemWidth(120 * Ui.Scale); Ui.InputInt("Dresser index", ref dresserIndex);
        ImGui.SetNextItemWidth(120 * Ui.Scale); Ui.InputInt("Item id (market test)", ref itemId);

        var kind = GameContainerIds.KindOf((uint)container) ?? ContainerKind.Inventory;
        var target = new SlotRef(kind, (uint)container, slot, kind == ContainerKind.Retainer ? GameInventoryScanner.ActiveRetainer().Id : 0);

        Ui.Header("Read");
        if (Ui.Button("Read slot")) Run(() => Describe(actions.ReadSlot(target)));
        ImGui.SameLine();
        if (Ui.Button("Read dresser index")) Run(() => Describe(actions.ReadSlot(SlotRef.Dresser(dresserIndex))));
        ImGui.SameLine();
        if (Ui.Button("Plate references")) Run(() =>
        {
            var plates = GameInventoryScanner.PlateItemIds();
            if (plates is null) return "Plates not loaded (open the dresser first)";
            var item = actions.ReadSlot(SlotRef.Dresser(dresserIndex));
            return item is null ? $"{plates.Count} plate item ids loaded; dresser index empty" : $"{plates.Count} plate item ids loaded; index {dresserIndex} ({db.Get(item.ItemId)?.Name}) referenced: {plates.Contains(item.ItemId)}";
        });
        ImGui.SameLine();
        if (Ui.Button("Context menu labels")) Run(() =>
        {
            var entries = context.ReadEntries(target);
            return entries.Count == 0 ? "no context entries" : string.Join("  ", entries.Select(e => $"[{e.Index}] {e.LabelId} '{e.Text}'{(e.Disabled ? " (disabled)" : "")}"));
        });

        Ui.Header("Spikes (one item each)");
        DrawDiscardSpike(target);
        ImGui.SameLine();
        if (Ui.ButtonColored("Restore dresser index", Ui.Warn)) RunAsync(async ct =>
        {
            var landed = await actions.RestoreFromDresserAsync(SlotRef.Dresser(dresserIndex), actions.ReadSlot(SlotRef.Dresser(dresserIndex))?.ItemId ?? 0, ct);
            return landed is null ? $"restore: False · {actions.LastFailure}" : $"restore: True · landed in {landed}";
        });
        ImGui.SameLine();
        if (Ui.ButtonColored("Retrieve materia", Ui.Warn)) Spike("materia", ct => actions.RetrieveMateriaAsync(target, actions.ReadSlot(target)?.ItemId ?? 0, ct));
        ImGui.SameLine();
        if (Ui.ButtonColored("Desynth slot", Ui.Warn)) Spike("desynth", ct => actions.DesynthAsync(target, actions.ReadSlot(target)?.ItemId ?? 0, ct));
        ImGui.SameLine();
        if (Ui.ButtonColored("Vendor sell slot", Ui.Ok)) Spike("sell", ct => actions.VendorSellAsync(target, actions.ReadSlot(target)?.ItemId ?? 0, ct));
        ImGui.SameLine();
        if (Ui.ButtonColored("Expert delivery slot", Ui.Info)) Spike("seals", ct => actions.ExpertDeliveryAsync(target, actions.ReadSlot(target)?.ItemId ?? 0, ct));

        Ui.Header("Integrations");
        if (Ui.Button("Allagan Tools status")) Run(() =>
        {
            if (!allagan.IsInstalled) return "Allagan Tools not installed/loaded";
            if (!allagan.IsAvailable) return "Allagan Tools installed but IPC says not initialised";
            var chars = allagan.Characters();
            var mine = allagan.Items(player.ContentId);
            var byContainer = string.Join(", ", mine.GroupBy(i => i.Slot.ContainerId).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"));
            return $"available · {chars.Count} characters/retainers · {mine.Count} cached items for this character · by kind: {string.Join(", ", mine.GroupBy(i => i.Slot.Kind).Select(g => $"{g.Key} {g.Count()}"))} · by raw container: {byContainer}";
        });
        ImGui.SameLine();
        if (Ui.Button("Universalis test")) RunAsync(async ct =>
        {
            if (itemId <= 0) return "Set an item id above first (e.g. 5 for Earth Shard)";
            var world = player.CurrentWorld.ValueNullable?.Name.ExtractText() ?? "";
            var p = await market.GetPricesAsync([(uint)itemId], world, ct);
            return p.TryGetValue((uint)itemId, out var mp) ? $"{world}: {db.Get((uint)itemId)?.Name} NQ {mp.MinNq:N0} HQ {mp.MinHq:N0}" : $"{world}: no price for {itemId}";
        });
        ImGui.SameLine();
        if (Ui.Button("Retired-currency gear count")) Run(() => $"{db.RetiredCurrencyGear().Count} items");
        ImGui.SameLine();
        if (Ui.Button("Recipe index sample")) Run(() => $"item {itemId} used in {db.RecipesUsing((uint)itemId).Count} recipes");

        Ui.Header("Callback values");
        var cb = config.Callbacks;
        var changed = false;
        ImGui.SetNextItemWidth(90 * Ui.Scale); var v = cb.YesNoConfirm; if (Ui.InputInt("SelectYesno", ref v)) { cb.YesNoConfirm = v; changed = true; }
        ImGui.SetNextItemWidth(90 * Ui.Scale); v = cb.MateriaRetrieveConfirm; if (Ui.InputInt("MateriaRetrieveDialog", ref v)) { cb.MateriaRetrieveConfirm = v; changed = true; }
        ImGui.SetNextItemWidth(90 * Ui.Scale); v = cb.SalvageConfirm; if (Ui.InputInt("SalvageDialog", ref v)) { cb.SalvageConfirm = v; changed = true; }
        ImGui.SetNextItemWidth(90 * Ui.Scale); v = cb.ExpertDeliverySelect; if (Ui.InputInt("GC supply select", ref v)) { cb.ExpertDeliverySelect = v; changed = true; }
        ImGui.SetNextItemWidth(90 * Ui.Scale); v = cb.ExpertDeliveryConfirm; if (Ui.InputInt("GC reward confirm", ref v)) { cb.ExpertDeliveryConfirm = v; changed = true; }
        var sell = cb.SellLabel; ImGui.SetNextItemWidth(160 * Ui.Scale); if (Ui.InputText("Sell label", "", ref sell, 32)) { cb.SellLabel = sell; changed = true; }
        var mat = cb.RetrieveMateriaLabel; ImGui.SetNextItemWidth(160 * Ui.Scale); if (Ui.InputText("Retrieve materia label", "", ref mat, 32)) { cb.RetrieveMateriaLabel = mat; changed = true; }
        var verified = config.SpikesVerified; if (ImGui.Checkbox("Spikes verified on this machine", ref verified)) { config.SpikesVerified = verified; changed = true; }
        if (changed) config.Save(PluginServices.PluginInterface);

        Ui.Header("Log");
        using var child = ImRaii.Child("##log", new Vector2(0, 0), true, ImGuiWindowFlags.None);
        foreach (var l in lines) ImGui.TextWrapped(l);
    }

    private string Describe(ScannedItem? item)
    {
        if (item is null) return "empty / unreadable";
        var info = db.Get(item.ItemId);
        return $"{info?.Name ?? "?"} (id {item.ItemId}) ×{item.Quantity}{(item.IsHq ? " HQ" : "")} materia {item.MateriaCount} dye {item.Stain0}/{item.Stain1} @ {item.Slot} · vendor {info?.VendorPrice}g · marketable {info?.IsMarketable} · untradeable {info?.IsUntradable} · unique {info?.IsUnique} · indisposable {info?.IsIndisposable} · cat {info?.UiCategory}";
    }

    private string? discardArmedFor;
    private DateTime discardArmedAt;
    private bool forceDangerous;

    /// <summary>Two clicks, names the item, and refuses anything the planner would hard-block unless forced.</summary>
    private void DrawDiscardSpike(SlotRef target)
    {
        var item = actions.ReadSlot(target);
        var info = item is null ? null : db.Get(item.ItemId);
        var armed = discardArmedFor is not null && discardArmedFor == target.ToString() && (DateTime.UtcNow - discardArmedAt).TotalSeconds < 6;
        var label = armed && info is not null ? $"Really discard {info.Name} ×{item!.Quantity}?" : "Discard slot";

        var dangerous = info is not null && (info.IsIndisposable || (info.IsUnique && info.IsUntradable) || info.IsNeverProposed
                                             || (!info.IsEquipment && info.IsUntradable && info.VendorPrice == 0));
        using (ImRaii.Disabled(item is null || (dangerous && !forceDangerous)))
        {
            if (Ui.ButtonColored(label, Ui.Danger, 260 * Ui.Scale))
            {
                if (!armed) { discardArmedFor = target.ToString(); discardArmedAt = DateTime.UtcNow; }
                else
                {
                    discardArmedFor = null;
                    var id = item!.ItemId;
                    Spike($"discard {info!.Name}", ct => actions.DiscardAsync(target, id, ct));
                }
            }
        }
        if (item is null) Ui.Tooltip("Slot is empty.");
        else if (dangerous) Ui.Tooltip($"{info!.Name} is hard-blocked (untradeable with no vendor value, unique, indisposable, or a protected category). Tick Force to override.");
        ImGui.SameLine();
        ImGui.Checkbox("Force", ref forceDangerous);
        Ui.Tooltip("Allow the spike to discard items the planner would never propose. Off by default for a reason.");
    }

    private void Spike(string name, Func<CancellationToken, Task<bool>> action) => RunAsync(async ct =>
    {
        var ok = await action(ct);
        return ok ? $"{name}: True" : $"{name}: False · {actions.LastFailure ?? "no reason recorded"}";
    });

    private void Run(Func<string> f) => framework.RunOnFrameworkThread(() =>

    {
        try { Log(f()); } catch (Exception ex) { Log($"ERROR {ex.GetType().Name}: {ex.Message}"); }
    });

    private void RunAsync(Func<CancellationToken, Task<string>> f) => Task.Run(async () =>
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Log(await f(cts.Token));
        }
        catch (Exception ex) { Log($"ERROR {ex.GetType().Name}: {ex.Message}"); }
    });
}
