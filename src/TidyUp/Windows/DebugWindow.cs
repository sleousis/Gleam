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

/// <summary>Verification tools. Each button does one thing to one item and says what happened.</summary>
public sealed class DebugWindow : Window
{
    private readonly IFramework framework;
    private readonly GameActions actions;
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
    private string? discardArmedFor;
    private DateTime discardArmedAt;
    private bool forceDangerous;

    public DebugWindow(IFramework framework, GameActions actions, GameInventoryScanner scanner, InventoryContextDriver context,
        ItemDatabase db, AllaganToolsSource allagan, IMarketPriceSource market, IPlayerState player, Configuration config)
        : base("Tidy Up Verification###TidyUpDebug")
    {
        this.framework = framework;
        this.actions = actions;
        this.context = context;
        this.db = db;
        this.allagan = allagan;
        this.market = market;
        this.player = player;
        this.config = config;
        Size = new Vector2(720, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    private void Log(string s)
    {
        lines.Insert(0, $"{DateTime.Now:HH:mm:ss}  {s}");
        if (lines.Count > 200) lines.RemoveAt(lines.Count - 1);
    }

    public override void Draw()
    {
        Ui.HintWrapped("These act on real items. Point them at junk. Run each once before trusting a full clean.");

        Ui.Section("Target");
        var w = 100 * Ui.Scale;
        ImGui.SetNextItemWidth(w); Ui.InputInt("Container", ref container);
        Ui.Tooltip("0–3 inventory pages · 3200–3500 armoury · 4000/4001/4100/4101 saddlebag · 10000–10006 retainer pages");
        ImGui.SameLine(); ImGui.SetNextItemWidth(w); Ui.InputInt("Slot", ref slot);
        ImGui.SameLine(); ImGui.SetNextItemWidth(w); Ui.InputInt("Dresser index", ref dresserIndex);

        var kind = GameContainerIds.KindOf((uint)container) ?? ContainerKind.Inventory;
        var target = new SlotRef(kind, (uint)container, slot, kind == ContainerKind.Retainer ? GameInventoryScanner.ActiveRetainer().Id : 0);
        var current = actions.ReadSlot(target);
        Ui.Hint(current is null ? "Slot is empty." : $"Slot holds {db.Get(current.ItemId)?.Name ?? "?"} × {current.Quantity}");

        Ui.Section("Read");
        if (Ui.Button("Slot details")) Run(() => Describe(actions.ReadSlot(target)));
        ImGui.SameLine();
        if (Ui.Button("Dresser index details")) Run(() => Describe(actions.ReadSlot(SlotRef.Dresser(dresserIndex))));
        ImGui.SameLine();
        if (Ui.Button("Plate references")) Run(() =>
        {
            var plates = GameInventoryScanner.PlateItemIds();
            if (plates is null) return "Plates not loaded. Open the dresser first.";
            var item = actions.ReadSlot(SlotRef.Dresser(dresserIndex));
            return item is null ? $"{plates.Count} plate item ids loaded; that dresser index is empty" : $"{plates.Count} plate item ids loaded; {db.Get(item.ItemId)?.Name} referenced: {plates.Contains(item.ItemId)}";
        });
        ImGui.SameLine();
        if (Ui.Button("Context menu labels")) Run(() =>
        {
            var entries = context.ReadEntries(target);
            return entries.Count == 0 ? "No context entries" : string.Join("   ", entries.Select(e => $"[{e.Index}] '{e.Text}'{(e.Disabled ? " (off)" : "")}"));
        });

        Ui.Section("Act (one item each)");
        DrawDiscardSpike(target, current);
        ImGui.SameLine();
        if (Ui.Button("Restore dresser index")) RunAsync(async ct =>
        {
            var landed = await actions.RestoreFromDresserAsync(SlotRef.Dresser(dresserIndex), actions.ReadSlot(SlotRef.Dresser(dresserIndex))?.ItemId ?? 0, ct);
            return landed is null ? $"restore: no · {actions.LastFailure}" : $"restore: yes · landed in {landed}";
        });
        ImGui.SameLine();
        if (Ui.Button("Retrieve materia")) Spike("materia", ct => actions.RetrieveMateriaAsync(target, actions.ReadSlot(target)?.ItemId ?? 0, ct));
        ImGui.SameLine();
        if (Ui.Button("Desynth")) Spike("desynth", ct => actions.DesynthAsync(target, actions.ReadSlot(target)?.ItemId ?? 0, ct));
        ImGui.SameLine();
        if (Ui.Button("Sell to vendor")) Spike("sell", ct => actions.VendorSellAsync(target, actions.ReadSlot(target)?.ItemId ?? 0, ct));
        ImGui.SameLine();
        if (Ui.Button("Expert delivery")) Spike("seals", ct => actions.ExpertDeliveryAsync(target, actions.ReadSlot(target)?.ItemId ?? 0, ct));

        Ui.Section("Integrations");
        if (Ui.Button("Allagan Tools")) Run(() =>
        {
            if (!allagan.IsInstalled) return "Allagan Tools is not installed or not loaded";
            if (!allagan.IsAvailable) return "Allagan Tools is installed but its IPC says not initialised yet";
            var chars = allagan.Characters();
            var mine = allagan.Items(player.ContentId);
            var byContainer = string.Join(", ", mine.GroupBy(i => i.Slot.ContainerId).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"));
            var known = GameInventoryScanner.KnownRetainers();
            var gameIds = string.Join(", ", known.Select(kv => $"{kv.Value}={kv.Key:X}"));
            var cacheOwners = string.Join(", ", chars.Where(c => c.CharacterId != player.ContentId)
                .Select(c => { var it = allagan.Items(c.CharacterId); return $"{c.CharacterId:X}:{it.Count} items, retainer pages {it.Count(i => i.Slot.Kind == ContainerKind.Retainer)}, owners {string.Join("/", it.Select(i => i.Slot.OwnerId.ToString("X")).Distinct().Take(3))}"; }));
            var (activeId, activeName) = GameInventoryScanner.ActiveRetainer();
            return $"connected · {mine.Count} cached items on this character by container: {byContainer} · game retainers: {gameIds} · active: {activeName}={activeId:X} · cache entries: {cacheOwners}";
        });
        ImGui.SameLine();
        ImGui.SetNextItemWidth(w); Ui.InputInt("##mid", ref itemId);
        ImGui.SameLine();
        if (Ui.Button("Universalis price")) RunAsync(async ct =>
        {
            if (itemId <= 0) return "Enter an item id first (5 is Earth Shard).";
            var world = player.CurrentWorld.ValueNullable?.Name.ExtractText() ?? "";
            var p = await market.GetPricesAsync([(uint)itemId], world, ct);
            return p.TryGetValue((uint)itemId, out var mp) ? $"{world}: {db.Get((uint)itemId)?.Name} · NQ {mp.MinNq:N0} · HQ {mp.MinHq:N0}" : $"{world}: no price for {itemId}";
        });
        ImGui.SameLine();
        if (Ui.Button("Indexes")) Run(() => $"{db.RetiredCurrencyGear().Count} retired-currency gear items · item {itemId} used in {db.RecipesUsing((uint)itemId).Count} recipes");

        Ui.Section("Dialog answers");
        var cb = config.Callbacks;
        var changed = false;
        var v = cb.YesNoConfirm; ImGui.SetNextItemWidth(70 * Ui.Scale); if (Ui.InputInt("Yes/No", ref v)) { cb.YesNoConfirm = v; changed = true; }
        ImGui.SameLine(); v = cb.MateriaRetrieveConfirm; ImGui.SetNextItemWidth(70 * Ui.Scale); if (Ui.InputInt("Materia", ref v)) { cb.MateriaRetrieveConfirm = v; changed = true; }
        ImGui.SameLine(); v = cb.SalvageConfirm; ImGui.SetNextItemWidth(70 * Ui.Scale); if (Ui.InputInt("Desynth", ref v)) { cb.SalvageConfirm = v; changed = true; }
        ImGui.SameLine(); v = cb.ExpertDeliverySelect; ImGui.SetNextItemWidth(70 * Ui.Scale); if (Ui.InputInt("GC select", ref v)) { cb.ExpertDeliverySelect = v; changed = true; }
        ImGui.SameLine(); v = cb.ExpertDeliveryConfirm; ImGui.SetNextItemWidth(70 * Ui.Scale); if (Ui.InputInt("GC confirm", ref v)) { cb.ExpertDeliveryConfirm = v; changed = true; }
        var sell = cb.SellLabel; ImGui.SetNextItemWidth(160 * Ui.Scale); if (Ui.InputText("Sell label", "", ref sell, 32)) { cb.SellLabel = sell; changed = true; }
        ImGui.SameLine(); var mat = cb.RetrieveMateriaLabel; ImGui.SetNextItemWidth(160 * Ui.Scale); if (Ui.InputText("Materia label", "", ref mat, 32)) { cb.RetrieveMateriaLabel = mat; changed = true; }
        var verified = config.SpikesVerified; if (ImGui.Checkbox("Verified on this machine", ref verified)) { config.SpikesVerified = verified; changed = true; }
        if (changed) config.Save(PluginServices.PluginInterface);

        Ui.Section("Log");
        using var child = ImRaii.Child("##log", new Vector2(0, 0), true, ImGuiWindowFlags.None);
        if (lines.Count == 0) Ui.Hint("Results appear here.");
        foreach (var l in lines) ImGui.TextWrapped(l);
    }

    /// <summary>Two clicks, names the item, refuses anything the planner would hard-block unless forced.</summary>
    private void DrawDiscardSpike(SlotRef target, ScannedItem? item)
    {
        var info = item is null ? null : db.Get(item.ItemId);
        var armed = discardArmedFor is not null && discardArmedFor == target.ToString() && (DateTime.UtcNow - discardArmedAt).TotalSeconds < 6;
        var label = armed && info is not null ? $"Really discard {info.Name}?" : "Discard";

        var dangerous = info is not null && (info.IsIndisposable || (info.IsUnique && info.IsUntradable) || info.IsNeverProposed
                                             || (!info.IsEquipment && info.IsUntradable && info.VendorPrice == 0));
        using (ImRaii.Disabled(item is null || (dangerous && !forceDangerous)))
        {
            if (Ui.PrimaryButton(label, armed ? 260 * Ui.Scale : 100 * Ui.Scale, danger: true))
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
        else if (dangerous) Ui.Tooltip($"{info!.Name} is protected by a hard rule. Tick Force to override.");
        ImGui.SameLine();
        ImGui.Checkbox("Force", ref forceDangerous);
        Ui.Tooltip("Let the spike discard items the planner would never propose. Off by default for a reason.");
    }

    private string Describe(ScannedItem? item)
    {
        if (item is null) return "empty or unreadable";
        var info = db.Get(item.ItemId);
        return $"{info?.Name ?? "?"} (id {item.ItemId}) × {item.Quantity}{(item.IsHq ? " HQ" : "")} · materia {item.MateriaCount} · dye {item.Stain0}/{item.Stain1} · {item.Slot} · vendor {info?.VendorPrice}g · marketable {info?.IsMarketable} · untradeable {info?.IsUntradable} · unique {info?.IsUnique} · indisposable {info?.IsIndisposable} · {info?.UiCategory}";
    }

    private void Spike(string name, Func<CancellationToken, Task<bool>> action) => RunAsync(async ct =>
    {
        var ok = await action(ct);
        return ok ? $"{name}: yes" : $"{name}: no · {actions.LastFailure ?? "no reason recorded"}";
    });

    private void Run(Func<string> f) => framework.RunOnFrameworkThread(() =>
    {
        try { Log(f()); } catch (Exception ex) { Log($"error {ex.GetType().Name}: {ex.Message}"); }
    });

    private void RunAsync(Func<CancellationToken, Task<string>> f) => Task.Run(async () =>
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Log(await f(cts.Token));
        }
        catch (Exception ex) { Log($"error {ex.GetType().Name}: {ex.Message}"); }
    });
}
