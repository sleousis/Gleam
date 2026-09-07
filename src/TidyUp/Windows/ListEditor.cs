using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using TidyUp.Core.Lists;
using TidyUp.Core.Model;
using TidyUp.Game;
using TidyUp.Services;

namespace TidyUp.Windows;

/// <summary>Search-and-add editor for one item list, with scope and HQ toggles per entry.</summary>
internal sealed class ListEditor
{
    private readonly ItemDatabase db;
    private readonly IconCache icons;
    private readonly Func<ItemList> list;
    private readonly string title;
    private readonly string help;
    private readonly Func<ulong> characterId;
    private readonly Action markDirty;

    private string search = string.Empty;
    private List<ItemInfo> results = new();
    private bool addForThisCharacter;

    public ListEditor(ItemDatabase db, IconCache icons, Func<ItemList> list, string title, string help, Func<ulong> characterId, Action markDirty)
    {
        this.db = db;
        this.icons = icons;
        this.list = list;
        this.title = title;
        this.help = help;
        this.characterId = characterId;
        this.markDirty = markDirty;
    }

    public void Draw()
    {
        Ui.Header(title);
        Ui.Muted2(help);

        ImGui.SetNextItemWidth(260 * Ui.Scale);
        if (Ui.InputText($"##s{title}", "Search an item to add…", ref search, 64))
            results = search.Length >= 2 ? db.Search(search, 30).ToList() : new List<ItemInfo>();
        ImGui.SameLine();
        ImGui.Checkbox($"Only this character##{title}", ref addForThisCharacter);

        if (results.Count > 0)
        {
            using var child = ImRaii.Child($"##r{title}", new Vector2(0, Math.Min(results.Count, 6) * 28 * Ui.Scale), true, ImGuiWindowFlags.None);
            foreach (var r in results)
            {
                var tex = icons.Get(r.IconId, false);
                if (!tex.IsNull) { ImGui.Image(tex, new Vector2(20 * Ui.Scale, 20 * Ui.Scale)); ImGui.SameLine(); }
                if (ImGui.Selectable($"{r.Name}##add{r.ItemId}", false, ImGuiSelectableFlags.None, Vector2.Zero))
                {
                    list().Add(r.ItemId, addForThisCharacter ? characterId() : null);
                    markDirty();
                    results.Clear();
                    search = string.Empty;
                    break;
                }
            }
        }

        var entries = list().Entries;
        if (entries.Count == 0) { Ui.Muted2("Empty."); return; }

        using var table = ImRaii.Table($"##t{title}", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings);
        if (!table) return;
        ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, 28 * Ui.Scale, 0);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 3f, 0);
        ImGui.TableSetupColumn("Scope", ImGuiTableColumnFlags.WidthFixed, 130 * Ui.Scale, 0);
        ImGui.TableSetupColumn("HQ too", ImGuiTableColumnFlags.WidthFixed, 60 * Ui.Scale, 0);
        ImGui.TableSetupColumn("Note", ImGuiTableColumnFlags.WidthStretch, 2f, 0);
        ImGui.TableSetupColumn("##rm", ImGuiTableColumnFlags.WidthFixed, 70 * Ui.Scale, 0);
        ImGui.TableHeadersRow();

        foreach (var e in entries.ToList())
        {
            var info = db.Get(e.ItemId);
            using var id = ImRaii.PushId($"{title}{e.ItemId}{e.CharacterId}");
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (info is not null) { var tex = icons.Get(info.IconId, false); if (!tex.IsNull) ImGui.Image(tex, new Vector2(22 * Ui.Scale, 22 * Ui.Scale)); }
            ImGui.TableNextColumn(); Ui.Text(info?.Name ?? $"item {e.ItemId}");
            ImGui.TableNextColumn(); Ui.Muted2(e.CharacterId is null ? "account-wide" : (e.CharacterId == characterId() ? "this character" : $"char {e.CharacterId:X}"));
            ImGui.TableNextColumn();
            var hq = e.IncludeHq;
            if (ImGui.Checkbox("##hq", ref hq)) { e.IncludeHq = hq; markDirty(); }
            ImGui.TableNextColumn(); Ui.Muted2(e.Note);
            ImGui.TableNextColumn();
            if (Ui.Button("Remove")) { entries.Remove(e); markDirty(); }
        }
    }
}
