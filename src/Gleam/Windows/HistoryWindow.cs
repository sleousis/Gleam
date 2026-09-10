using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Gleam.Core.Logging;
using Gleam.Core.Model;
using Gleam.Game;
using Gleam.Services;

namespace Gleam.Windows;

/// <summary>Everything Gleam ever destroyed, sold, turned in or desynthed, with how to get it back.</summary>
public sealed class HistoryWindow
{
    private readonly IRunLog runLog;
    private readonly IMoveLog moveLog;
    private IReadOnlyList<MoveLogEntry> moves = Array.Empty<MoveLogEntry>();
    private bool showMoves;

    private static readonly IReadOnlyList<(int, string)> Views = [(0, "Cleaned"), (1, "Moved")];
    private readonly ItemDatabase db;
    private readonly IconCache icons;
    private IReadOnlyList<RunLogEntry> entries = Array.Empty<RunLogEntry>();
    private string search = string.Empty;
    private bool loading;

    /// <summary>Set by the host window: the way back to the list.</summary>
    public Action? Back { get; set; }

    public HistoryWindow(IRunLog runLog, IMoveLog moveLog, ItemDatabase db, IconCache icons)
    {
        this.runLog = runLog;
        this.moveLog = moveLog;
        this.db = db;
        this.icons = icons;
    }

    /// <summary>Called when the panel comes into view.</summary>
    public void OnShown() => Reload();

    private void Reload()
    {
        if (loading) return;
        loading = true;
        // What the organizer moved was written down but never shown anywhere. It is now.
        _ = moveLog.ReadAllAsync().ContinueWith(t => { if (t.IsCompletedSuccessfully) moves = t.Result.OrderByDescending(e => e.At).ToList(); });
        _ = runLog.ReadAllAsync().ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) entries = t.Result.OrderByDescending(e => e.At).ToList();
            loading = false;
            // A reload replaces every row, so let the new list read down the page instead of appearing whole.
            staggerAt = 0;
        });
    }

    private double staggerAt;
    private string lastSearch = string.Empty;

    /// <summary>Rows fade in one after another after a reload or a new search; only the first screenful.</summary>
    private float RowAlpha(int index)
    {
        if (Ui.Reduced) return 1f;
        var elapsed = ImGui.GetTime() - staggerAt - Math.Min(index, 12) * 0.011;
        return Ui.EaseOut((float)Math.Clamp(elapsed / 0.16, 0, 1));
    }

    public void Draw()
    {
        var onRecord = (int)Ui.Count("histCount", entries.Count);
        Ui.Header(icons.LogoSmall, "What Gleam did", $"{onRecord} item{(onRecord == 1 ? "" : "s")} on record");
        if (Back is not null) { if (Ui.BackLink()) Back(); }
        Ui.Gap(0.4f);
        Ui.SearchBox("##hs", ref search, 260 * Ui.Scale);
        ImGui.SameLine();
        if (Ui.IconButton(Dalamud.Interface.FontAwesomeIcon.Sync, "Look again", busy: loading)) Reload();
        ImGui.SameLine();
        var which = showMoves ? 1 : 0;
        if (Ui.Segmented("##histview", ref which, Views)) showMoves = which == 1;
        if (showMoves) { DrawMoves(); return; }

        var rows = entries.Where(e => string.IsNullOrWhiteSpace(search)
            || e.ItemName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || e.CharacterName.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();

        // A discard under "Discard all" is logged with no value, which made everything destroyed add up to
        // nothing. The vendor price is the least it was worth.
        var destroyed = Ui.Count("histLost", rows.Where(e => e.Action == ActionKind.Discard)
            .Sum(e => e.ValueGil > 0 ? e.ValueGil : (long)(db.Get(e.ItemId)?.VendorPrice ?? 0) * e.Quantity));
        var recovered = Ui.Count("histGot", rows.Where(e => e.Action == ActionKind.VendorSell).Sum(e => e.ValueGil));
        var summary = loading ? "Loading…" : $"Recovered {Ui.Gil(recovered)} · destroyed {Ui.Gil(destroyed)} of vendor value";
        ImGui.SameLine();
        // Reserve the width of the settled sentence, so a counting total does not drag the line sideways.
        Ui.RightAlign(ImGui.CalcTextSize($"Recovered {Ui.Gil(rows.Sum(e => e.ValueGil))} · destroyed {Ui.Gil(destroyed)} of vendor value", false, 0).X);
        Ui.TextSwap("histSummary", summary, Ui.Muted * new Vector4(1, 1, 1, 0.8f));
        Ui.Gap(0.5f);

        if (rows.Count == 0)
        {
            if (entries.Count == 0) Ui.EmptyState(icons.LogoMedium, "Nothing yet.", "Every item Gleam discards, sells or turns in is listed here.");
            else Ui.EmptyState(icons.LogoMedium, "Nothing matches your search.");
            return;
        }

        using var table = ImRaii.Table("##hist", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.PadOuterX);
        if (!table) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("##when", ImGuiTableColumnFlags.WidthFixed, 110 * Ui.Scale, 0);
        ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, 30 * Ui.Scale, 0);
        ImGui.TableSetupColumn("##item", ImGuiTableColumnFlags.WidthStretch, 5f, 0);
        ImGui.TableSetupColumn("##action", ImGuiTableColumnFlags.WidthFixed, 110 * Ui.Scale, 0);
        ImGui.TableSetupColumn("##back", ImGuiTableColumnFlags.WidthStretch, 4f, 0);

        ImGui.TableNextRow(ImGuiTableRowFlags.None, 24 * Ui.Scale);
        foreach (var title in new[] { "When", "", "Item", "Action", "To get it back" })
        {
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            Ui.Hint(title);
        }

        if (staggerAt <= 0 || lastSearch != search) { staggerAt = ImGui.GetTime(); lastSearch = search; }

        var index = 0;
        foreach (var e in rows)
        {
            var info = db.Get(e.ItemId);
            using var fade = ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * RowAlpha(index++));
            ImGui.TableNextRow(ImGuiTableRowFlags.None, 28 * Ui.Scale);
            ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); Ui.Hint(e.At.ToLocalTime().ToString("MMM d, HH:mm"));
            ImGui.TableNextColumn();
            if (info is not null)
            {
                Ui.ImageRounded(icons.Get(info.IconId, e.IsHq), new Vector2(24 * Ui.Scale, 24 * Ui.Scale), 4 * Ui.Scale, $"icon:{info.IconId}");
            }
            ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding();
            Ui.Text(e.ItemName + (e.IsHq ? " " : ""));
            ImGui.SameLine(); Ui.Hint($"{(e.Quantity > 1 ? $"× {e.Quantity} · " : "")}{e.CharacterName} · {e.Container.DisplayName().ToLowerInvariant()}");
            ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); Ui.ActionLabel(e.Action);
            ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); Ui.Hint(ReacquireHint(info));
        }
    }

    /// <summary>What the organizer moved, newest first: where each stack came from and where it went.</summary>
    private void DrawMoves()
    {
        Ui.Gap(0.5f);
        var rows = moves.Where(e => string.IsNullOrWhiteSpace(search)
            || e.ItemName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || e.CharacterName.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        if (rows.Count == 0)
        {
            Ui.EmptyState(icons.LogoMedium, moves.Count == 0 ? "Nothing moved yet." : "Nothing matches your search.");
            return;
        }

        using var table = ImRaii.Table("##moves", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.PadOuterX);
        if (!table) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("##when", ImGuiTableColumnFlags.WidthFixed, 110 * Ui.Scale, 0);
        ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, 30 * Ui.Scale, 0);
        ImGui.TableSetupColumn("##item", ImGuiTableColumnFlags.WidthStretch, 5f, 0);
        ImGui.TableSetupColumn("##where", ImGuiTableColumnFlags.WidthStretch, 4f, 0);
        ImGui.TableNextRow(ImGuiTableRowFlags.None, 24 * Ui.Scale);
        foreach (var title in new[] { "When", "", "Item", "Where it went" })
        {
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            Ui.Hint(title);
        }

        var index = 0;
        foreach (var e in rows)
        {
            var info = db.Get(e.ItemId);
            using var fade = ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * RowAlpha(index++));
            ImGui.TableNextRow(ImGuiTableRowFlags.None, 28 * Ui.Scale);
            ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); Ui.Hint(e.At.ToLocalTime().ToString("MMM d, HH:mm"));
            ImGui.TableNextColumn();
            if (info is not null) Ui.ImageRounded(icons.Get(info.IconId, e.IsHq), new Vector2(24 * Ui.Scale, 24 * Ui.Scale), 4 * Ui.Scale, $"icon:{info.IconId}");
            ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding();
            Ui.Text(e.ItemName + (e.IsHq ? " " : ""));
            ImGui.SameLine(); Ui.Hint($"{(e.Quantity > 1 ? $"× {e.Quantity} · " : "")}{e.CharacterName}");
            ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding();
            Ui.Icon(Ui.ContainerIcon(e.ToKind), Ui.Muted);
            ImGui.SameLine(0, 6 * Ui.Scale);
            Ui.Hint($"{e.FromKind.DisplayName()}  →  {e.ToKind.DisplayName()}{(e.Leg == "RelayOut" ? ", on the way" : "")}");
        }
    }

    private static string ReacquireHint(ItemInfo? info)
    {
        if (info is null) return string.Empty;
        if (info.IsVendorBuyable) return $"Vendors sell it for {info.BuyPrice:N0}g";
        if (info.IsMarketable) return "Market board";
        if (info.IsUnique && info.IsUntradable) return "Unique and untradeable. Usually cannot be had again";
        if (info.IsUntradable) return "Untradeable. Comes from quests, duties or events";
        return string.Empty;
    }
}
