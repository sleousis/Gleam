using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using TidyUp.Core.Logging;
using TidyUp.Core.Model;
using TidyUp.Game;
using TidyUp.Services;

namespace TidyUp.Windows;

/// <summary>Everything Tidy Up ever destroyed, sold, turned in or desynthed, with how to get it back.</summary>
public sealed class HistoryWindow : Window
{
    private readonly IRunLog runLog;
    private readonly ItemDatabase db;
    private readonly IconCache icons;
    private IReadOnlyList<RunLogEntry> entries = Array.Empty<RunLogEntry>();
    private string search = string.Empty;
    private bool loading;
    private DateTime loadedAt = DateTime.MinValue;

    public HistoryWindow(IRunLog runLog, ItemDatabase db, IconCache icons) : base("Tidy Up History###TidyUpHistory")
    {
        this.runLog = runLog;
        this.db = db;
        this.icons = icons;
        Size = new Vector2(900, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnOpen() => Reload();

    private void Reload()
    {
        if (loading) return;
        loading = true;
        _ = runLog.ReadAllAsync().ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) entries = t.Result.OrderByDescending(e => e.At).ToList();
            loadedAt = DateTime.UtcNow;
            loading = false;
        });
    }

    public override void Draw()
    {
        ImGui.SetNextItemWidth(240 * Ui.Scale);
        Ui.InputText("##hs", "Search item or character…", ref search, 64);
        ImGui.SameLine();
        if (Ui.Button("Reload")) Reload();
        ImGui.SameLine();
        Ui.Muted2($"{entries.Count} entries{(loading ? " · loading…" : "")}");

        var rows = entries.Where(e => string.IsNullOrWhiteSpace(search)
            || e.ItemName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || e.CharacterName.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();

        var destroyed = rows.Where(e => e.Action == Core.Model.ActionKind.Discard).Sum(e => e.ValueGil);
        var recovered = rows.Where(e => e.Action == Core.Model.ActionKind.VendorSell).Sum(e => e.ValueGil);
        Ui.Muted2($"{rows.Count} shown · {rows.Sum(e => e.Quantity):N0} items · recovered {Ui.Gil(recovered)} · destroyed {Ui.Gil(destroyed)} vendor value");

        using var table = ImRaii.Table("##hist", 8, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings);
        if (!table) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthFixed, 120 * Ui.Scale, 0);
        ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthFixed, 130 * Ui.Scale, 0);
        ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, 30 * Ui.Scale, 0);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 3f, 0);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 44 * Ui.Scale, 0);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 80 * Ui.Scale, 0);
        ImGui.TableSetupColumn("From", ImGuiTableColumnFlags.WidthFixed, 120 * Ui.Scale, 0);
        ImGui.TableSetupColumn("Get it back", ImGuiTableColumnFlags.WidthStretch, 3f, 0);
        ImGui.TableHeadersRow();

        foreach (var e in rows)
        {
            var info = db.Get(e.ItemId);
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); Ui.Text(e.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            ImGui.TableNextColumn(); Ui.Text(e.CharacterName);
            ImGui.TableNextColumn();
            if (info is not null)
            {
                var tex = icons.Get(info.IconId, e.IsHq);
                if (!tex.IsNull) ImGui.Image(tex, new Vector2(22 * Ui.Scale, 22 * Ui.Scale));
            }
            ImGui.TableNextColumn(); Ui.Text(e.ItemName + (e.IsHq ? " " : ""));
            ImGui.TableNextColumn(); Ui.Text($"× {e.Quantity}");
            ImGui.TableNextColumn(); Ui.TextColored(Ui.ActionColor(e.Action), e.Action.Label());
            ImGui.TableNextColumn(); Ui.Muted2(e.Container.DisplayName());
            ImGui.TableNextColumn();
            Ui.Muted2(ReacquireHint(info));
        }
    }

    private static string ReacquireHint(Core.Model.ItemInfo? info)
    {
        if (info is null) return string.Empty;
        if (info.IsVendorBuyable) return $"Sold by gil vendors for {info.BuyPrice:N0}g";
        if (info.IsMarketable) return "Buy on the market board";
        if (info.IsUntradable) return "Untradeable: quests, duties or events only";
        return string.Empty;
    }
}
