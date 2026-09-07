using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
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
        if (Ui.Button("Context menu labels")) Run(() => DumpContextLabels(target));

        Ui.Header("Spikes (one item each)");
        if (Ui.ButtonColored("Discard slot", Ui.Danger)) RunAsync(async ct => $"discard: {await actions.DiscardAsync(target, actions.ReadSlot(target)?.ItemId ?? 0, ct)}");
        ImGui.SameLine();
        if (Ui.ButtonColored("Restore dresser index", Ui.Warn)) RunAsync(async ct => $"restore: {await actions.RestoreFromDresserAsync(SlotRef.Dresser(dresserIndex), actions.ReadSlot(SlotRef.Dresser(dresserIndex))?.ItemId ?? 0, ct)}");
        ImGui.SameLine();
        if (Ui.ButtonColored("Retrieve materia", Ui.Warn)) RunAsync(async ct => $"materia: {await actions.RetrieveMateriaAsync(target, actions.ReadSlot(target)?.ItemId ?? 0, ct)}");
        ImGui.SameLine();
        if (Ui.ButtonColored("Desynth slot", Ui.Warn)) RunAsync(async ct => $"desynth: {await actions.DesynthAsync(target, actions.ReadSlot(target)?.ItemId ?? 0, ct)}");
        ImGui.SameLine();
        if (Ui.ButtonColored("Vendor sell slot", Ui.Ok)) RunAsync(async ct => $"sell: {await actions.VendorSellAsync(target, actions.ReadSlot(target)?.ItemId ?? 0, ct)}");
        ImGui.SameLine();
        if (Ui.ButtonColored("Expert delivery slot", Ui.Info)) RunAsync(async ct => $"seals: {await actions.ExpertDeliveryAsync(target, actions.ReadSlot(target)?.ItemId ?? 0, ct)}");

        Ui.Header("Integrations");
        if (Ui.Button("Allagan Tools status")) Run(() =>
        {
            if (!allagan.IsInstalled) return "Allagan Tools not installed/loaded";
            if (!allagan.IsAvailable) return "Allagan Tools installed but IPC says not initialised";
            var chars = allagan.Characters();
            var mine = allagan.Items(player.ContentId);
            return $"available · {chars.Count} characters/retainers · {mine.Count} cached items for this character · by kind: {string.Join(", ", mine.GroupBy(i => i.Slot.Kind).Select(g => $"{g.Key} {g.Count()}"))}";
        });
        ImGui.SameLine();
        if (Ui.Button("Universalis test")) RunAsync(async ct =>
        {
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

    private unsafe string DumpContextLabels(SlotRef target)
    {
        var agent = AgentModule.Instance()->GetAgentInventoryContext();
        if (agent == null) return "no agent";
        agent->OpenForItemSlot((InventoryType)target.ContainerId, target.Slot, 0, 0);
        var infos = agent->ContextCallbackInfos;
        var count = agent->ContextItemCount;
        var addon = db;
        var parts = new List<string>();
        for (var i = 0; i < Math.Min(count, 32); i++)
        {
            var label = infos[i].LabelId;
            var text = PluginServices.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>()!.TryGetRow(label, out var row) ? row.Text.ExtractText() : "?";
            parts.Add($"[{i}] {label} '{text}'{(agent->IsContextItemDisabled(i) ? " (disabled)" : "")}");
        }
        var ctx = AddonDriver.GetAddon("ContextMenu");
        if (ctx != null && ctx->IsVisible) ctx->Close(true);
        return count == 0 ? "no context entries" : string.Join("  ", parts);
    }

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
