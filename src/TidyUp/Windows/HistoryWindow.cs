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

    public HistoryWindow(IRunLog runLog, ItemDatabase db, IconCache icons) : base("Tidy Up History###TidyUpHistory")
    {
        this.runLog = runLog;
        this.db = db;
        this.icons = icons;
        Size = new Vector2(820, 500);
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
            loading = false;
        });
    }

    public override void Draw()
    {
        ImGui.SetNextItemWidth(260 * Ui.Scale);
        Ui.InputText("##hs", "Search", ref search, 64);
        ImGui.SameLine();
        if (Ui.LinkButton("Reload")) Reload();

        var rows = entries.Where(e => string.IsNullOrWhiteSpace(search)
            || e.ItemName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || e.CharacterName.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();

        var destroyed = rows.Where(e => e.Action == ActionKind.Discard).Sum(e => e.ValueGil);
        var recovered = rows.Where(e => e.Action == ActionKind.VendorSell).Sum(e => e.ValueGil);
        var summary = $"{rows.Count} entries · recovered {Ui.Gil(recovered)} · destroyed {Ui.Gil(destroyed)} vendor value";
        ImGui.SameLine();
        Ui.RightAlign(ImGui.CalcTextSize(summary, false, 0).X);
        Ui.Hint(loading ? "loading…" : summary);
        Ui.Gap(0.5f);

        if (rows.Count == 0)
        {
            Ui.Gap(3);
            var msg = entries.Count == 0 ? "Nothing yet. Every item Tidy Up acts on will be listed here." : "Nothing matches your search.";
            ImGui.SetCursorPosX(Math.Max(0, (ImGui.GetWindowWidth() - ImGui.CalcTextSize(msg, false, 0).X) / 2));
            Ui.Hint(msg);
            return;
        }

        using var table = ImRaii.Table("##hist", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.PadOuterX);
        if (!table) return;
        ImGui.TableSetupScrollFreeze(0, 0);
        ImGui.TableSetupColumn("##when", ImGuiTableColumnFlags.WidthFixed, 110 * Ui.Scale, 0);
        ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, 30 * Ui.Scale, 0);
        ImGui.TableSetupColumn("##item", ImGuiTableColumnFlags.WidthStretch, 5f, 0);
        ImGui.TableSetupColumn("##action", ImGuiTableColumnFlags.WidthFixed, 90 * Ui.Scale, 0);
        ImGui.TableSetupColumn("##back", ImGuiTableColumnFlags.WidthStretch, 4f, 0);

        foreach (var e in rows)
        {
            var info = db.Get(e.ItemId);
            ImGui.TableNextRow(ImGuiTableRowFlags.None, 28 * Ui.Scale);
            ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); Ui.Hint(e.At.ToLocalTime().ToString("MMM d, HH:mm"));
            ImGui.TableNextColumn();
            if (info is not null)
            {
                var tex = icons.Get(info.IconId, e.IsHq);
                if (!tex.IsNull) ImGui.Image(tex, new Vector2(24 * Ui.Scale, 24 * Ui.Scale));
            }
            ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding();
            Ui.Text(e.ItemName + (e.IsHq ? " " : ""));
            ImGui.SameLine(); Ui.Hint($"× {e.Quantity} · {e.CharacterName} · {e.Container.DisplayName().ToLowerInvariant()}");
            ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); Ui.TextColored(Ui.ActionColor(e.Action), e.Action.Label());
            ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); Ui.Hint(ReacquireHint(info));
        }
    }

    private static string ReacquireHint(ItemInfo? info)
    {
        if (info is null) return string.Empty;
        if (info.IsVendorBuyable) return $"Gil vendors sell it for {info.BuyPrice:N0}g";
        if (info.IsMarketable) return "Market board";
        if (info.IsUntradable) return "Untradeable: quests, duties or events";
        return string.Empty;
    }
}
