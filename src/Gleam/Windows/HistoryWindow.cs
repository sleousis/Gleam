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

    /// <summary>Set by the host window: the stats page.</summary>
    public Action? OpenStats { get; set; }

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

    private static readonly string[] CleanedTitles = ["When", "", "Item", "Action", "To get it back"];
    private static readonly string[] MovedTitles = ["When", "", "Item", "Where it went"];

    /// <summary>One cleaned item as the table draws it, looked up and worded once rather than on every frame.</summary>
    private sealed class CleanedRow(RunLogEntry e, ItemInfo? info)
    {
        public readonly RunLogEntry Entry = e;
        public readonly ItemInfo? Info = info;
        public readonly string When = e.At.ToLocalTime().ToString("MMM d, HH:mm");
        public readonly string? IconKey = info is null ? null : $"icon:{info.IconId}";
        public readonly string Name = e.ItemName + (e.IsHq ? " " : "");
        public readonly string Detail = $"{(e.Quantity > 1 ? $"× {e.Quantity} · " : "")}{e.CharacterName} · {e.Container.DisplayName().ToLowerInvariant()}";
        public readonly string Back = ReacquireHint(info);
        /// <summary>
        /// What a discard was worth. A discard under "Discard all" is logged with no value, which made everything
        /// destroyed add up to nothing; the vendor price is the least it was worth.
        /// </summary>
        public readonly long Lost = e.Action != ActionKind.Discard ? 0 : e.ValueGil > 0 ? e.ValueGil : (long)(info?.VendorPrice ?? 0) * e.Quantity;
    }

    /// <summary>One move as the table draws it.</summary>
    private sealed class MovedRow(MoveLogEntry e, ItemInfo? info)
    {
        public readonly MoveLogEntry Entry = e;
        public readonly ItemInfo? Info = info;
        public readonly string When = e.At.ToLocalTime().ToString("MMM d, HH:mm");
        public readonly string? IconKey = info is null ? null : $"icon:{info.IconId}";
        public readonly string Name = e.ItemName + (e.IsHq ? " " : "");
        public readonly string Detail = $"{(e.Quantity > 1 ? $"× {e.Quantity} · " : "")}{e.CharacterName}";
        public readonly string Route = $"{e.FromKind.DisplayName()}  →  {e.ToKind.DisplayName()}{(e.Leg == "RelayOut" ? ", on the way" : "")}";
    }

    private static bool Matches(string itemName, string characterName, string search) =>
        string.IsNullOrWhiteSpace(search)
        || itemName.Contains(search, StringComparison.OrdinalIgnoreCase)
        || characterName.Contains(search, StringComparison.OrdinalIgnoreCase);

    // Worked out once per list and search rather than once per frame: filtering the whole history, summing it
    // three times over the item sheet and wording every row was the most expensive thing this page did.
    private IReadOnlyList<RunLogEntry>? cleanedFrom;
    private List<CleanedRow> cleanedAll = new();
    private string? cleanedSearch;
    private List<CleanedRow> cleanedShown = new();
    private long cleanedLost, cleanedGot, cleanedValue;

    private IReadOnlyList<MoveLogEntry>? movedFrom;
    private List<MovedRow> movedAll = new();
    private string? movedSearch;
    private List<MovedRow> movedShown = new();

    private void RefreshCleaned()
    {
        // A reload swaps the whole list for a new one, so the reference says whether anything changed.
        var source = entries;
        if (!ReferenceEquals(source, cleanedFrom))
        {
            cleanedFrom = source;
            cleanedAll = source.Select(e => new CleanedRow(e, db.Get(e.ItemId))).ToList();
            cleanedSearch = null;
        }
        if (cleanedSearch == search) return;
        cleanedSearch = search;
        cleanedShown = cleanedAll.Where(r => Matches(r.Entry.ItemName, r.Entry.CharacterName, search)).ToList();
        cleanedLost = cleanedShown.Sum(r => r.Lost);
        cleanedGot = cleanedShown.Where(r => r.Entry.Action == ActionKind.VendorSell).Sum(r => r.Entry.ValueGil);
        cleanedValue = cleanedShown.Sum(r => r.Entry.ValueGil);
    }

    private void RefreshMoved()
    {
        var source = moves;
        if (!ReferenceEquals(source, movedFrom))
        {
            movedFrom = source;
            movedAll = source.Select(e => new MovedRow(e, db.Get(e.ItemId))).ToList();
            movedSearch = null;
        }
        if (movedSearch == search) return;
        movedSearch = search;
        movedShown = movedAll.Where(r => Matches(r.Entry.ItemName, r.Entry.CharacterName, search)).ToList();
    }

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
        if (OpenStats is not null) { ImGui.SameLine(0, 18 * Ui.Scale); if (Ui.LinkButton("See the numbers")) OpenStats(); }
        Ui.Gap(0.4f);
        Ui.SearchBox("##hs", ref search, 260 * Ui.Scale);
        ImGui.SameLine();
        if (Ui.IconButton(Dalamud.Interface.FontAwesomeIcon.Sync, "Look again", busy: loading)) Reload();
        ImGui.SameLine();
        var which = showMoves ? 1 : 0;
        if (Ui.Segmented("##histview", ref which, Views)) showMoves = which == 1;
        if (showMoves) { DrawMoves(); return; }

        RefreshCleaned();
        var rows = cleanedShown;

        var destroyed = Ui.Count("histLost", cleanedLost);
        var recovered = Ui.Count("histGot", cleanedGot);
        var summary = loading ? "Loading…" : $"Recovered {Ui.Gil(recovered)} · destroyed {Ui.Gil(destroyed)} of vendor value";
        ImGui.SameLine();
        // Reserve the width of the settled sentence, so a counting total does not drag the line sideways.
        Ui.RightAlign(ImGui.CalcTextSize($"Recovered {Ui.Gil(cleanedValue)} · destroyed {Ui.Gil(destroyed)} of vendor value", false, 0).X);
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
        foreach (var title in CleanedTitles)
        {
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            Ui.Hint(title);
        }

        if (staggerAt <= 0 || lastSearch != search) { staggerAt = ImGui.GetTime(); lastSearch = search; }

        // Only the rows in view are drawn; the ones above and below are stood in by empty space.
        using var clip = new Ui.RowClipper(rows.Count);
        while (clip.Step())
            for (var i = clip.Start; i < clip.End; i++)
            {
                var r = rows[i];
                var e = r.Entry;
                using var fade = ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * RowAlpha(i));
                ImGui.TableNextRow(ImGuiTableRowFlags.None, 28 * Ui.Scale);
                ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); Ui.Hint(r.When);
                ImGui.TableNextColumn();
                if (r.Info is not null)
                {
                    Ui.ImageRounded(icons.Get(r.Info.IconId, e.IsHq), new Vector2(24 * Ui.Scale, 24 * Ui.Scale), 4 * Ui.Scale, r.IconKey);
                }
                ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding();
                Ui.Text(r.Name);
                ImGui.SameLine(); Ui.Hint(r.Detail);
                ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); Ui.ActionLabel(e.Action);
                ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); Ui.Hint(r.Back);
            }
    }

    /// <summary>What the organizer moved, newest first: where each stack came from and where it went.</summary>
    private void DrawMoves()
    {
        Ui.Gap(0.5f);
        RefreshMoved();
        var rows = movedShown;
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
        foreach (var title in MovedTitles)
        {
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            Ui.Hint(title);
        }

        using var clip = new Ui.RowClipper(rows.Count);
        while (clip.Step())
            for (var i = clip.Start; i < clip.End; i++)
            {
                var r = rows[i];
                var e = r.Entry;
                using var fade = ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * RowAlpha(i));
                ImGui.TableNextRow(ImGuiTableRowFlags.None, 28 * Ui.Scale);
                ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); Ui.Hint(r.When);
                ImGui.TableNextColumn();
                if (r.Info is not null) Ui.ImageRounded(icons.Get(r.Info.IconId, e.IsHq), new Vector2(24 * Ui.Scale, 24 * Ui.Scale), 4 * Ui.Scale, r.IconKey);
                ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding();
                Ui.Text(r.Name);
                ImGui.SameLine(); Ui.Hint(r.Detail);
                ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding();
                Ui.Icon(Ui.ContainerIcon(e.ToKind), Ui.Muted);
                ImGui.SameLine(0, 6 * Ui.Scale);
                Ui.Hint(r.Route);
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
