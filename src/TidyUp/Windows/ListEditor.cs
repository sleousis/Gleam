using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using TidyUp.Core.Lists;
using TidyUp.Core.Model;
using TidyUp.Game;
using TidyUp.Services;

namespace TidyUp.Windows;

/// <summary>Search-and-add editor for one item list. Entries show icon, name, scope; extras live behind hover.</summary>
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
        Ui.Section(title);
        Ui.Hint(help);

        ImGui.SetNextItemWidth(240 * Ui.Scale);
        if (Ui.InputText($"##s{title}", "Add an item…", ref search, 64))
            results = search.Length >= 2 ? db.Search(search, 30).ToList() : new List<ItemInfo>();
        ImGui.SameLine();
        Ui.Check($"This character only##{title}", ref addForThisCharacter);

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
        if (entries.Count == 0) { Ui.Hint("Empty."); return; }

        using var table = ImRaii.Table($"##t{title}", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.PadOuterX);
        if (!table) return;
        ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, 28 * Ui.Scale, 0);
        ImGui.TableSetupColumn("##item", ImGuiTableColumnFlags.WidthStretch, 4f, 0);
        ImGui.TableSetupColumn("##hq", ImGuiTableColumnFlags.WidthFixed, 90 * Ui.Scale, 0);
        ImGui.TableSetupColumn("##rm", ImGuiTableColumnFlags.WidthFixed, 70 * Ui.Scale, 0);

        foreach (var e in entries.ToList())
        {
            var info = db.Get(e.ItemId);
            using var id = ImRaii.PushId($"{title}{e.ItemId}{e.CharacterId}");
            ImGui.TableNextRow(ImGuiTableRowFlags.None, 28 * Ui.Scale);
            ImGui.TableNextColumn();
            if (info is not null) { var tex = icons.Get(info.IconId, false); if (!tex.IsNull) ImGui.Image(tex, new Vector2(22 * Ui.Scale, 22 * Ui.Scale)); }
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            Ui.Text(info?.Name ?? $"item {e.ItemId}");
            ImGui.SameLine();
            Ui.Hint(e.CharacterId is null ? "account" : (e.CharacterId == characterId() ? "this character" : $"char {e.CharacterId:X}"));
            if (!string.IsNullOrEmpty(e.Note)) Ui.Tooltip(e.Note);
            ImGui.TableNextColumn();
            var hq = e.IncludeHq;
            if (Ui.Check("HQ too", ref hq)) { e.IncludeHq = hq; markDirty(); }
            ImGui.TableNextColumn();
            if (Ui.LinkButton("Remove")) { entries.Remove(e); markDirty(); }
        }
    }
}
