using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using TidyUp.Core.Model;
using TidyUp.Core.Rules;

namespace TidyUp.Windows;

/// <summary>The folds under "More". Each one is a handful of switches a player can understand without knowing the plugin's insides.</summary>
public sealed partial class SettingsWindow
{
    // ---------- What the rules look for ----------

    private void DrawRules()
    {
        var p = Editing;
        foreach (var rule in RuleEngine.AllRules)
        {
            using var id = ImRaii.PushId(rule.Id);
            var on = p.EnabledRules.Contains(rule.Id);
            if (Ui.Check(rule.Name, ref on)) { if (on) p.EnabledRules.Add(rule.Id); else p.EnabledRules.Remove(rule.Id); dirty = true; }
            Ui.Tooltip(rule.Description);
        }

        Ui.Gap(0.3f);
        ImGui.AlignTextToFramePadding();
        Ui.Hint("Leave stacks of");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(70 * Ui.Scale);
        var guard = p.LargeStackGuard;
        if (ImGui.InputInt("##bigstack", ref guard, 0, 0, "%d", ImGuiInputTextFlags.None)) { p.LargeStackGuard = Math.Clamp(guard, 0, 9999); dirty = true; }
        Ui.Tooltip("A big stack is usually a hoard, not junk. Rules skip these. You can still tick them by hand. 0 turns this off.");
        ImGui.SameLine();
        Ui.Hint(guard == 0 ? "or more alone (off)" : "or more alone");
    }

    // ---------- Retainers ----------

    private void DrawContainers()
    {
        var p = Editing;
        var known = coordinator.RetainerNames;
        if (known.Count == 0) { Ui.Hint("Summon a retainer once and they will appear here."); return; }
        foreach (var (id, name) in known)
        {
            var included = !p.ExcludedRetainerIds.Contains(id);
            if (Ui.Check($"{name}##ret{id}", ref included)) { if (included) p.ExcludedRetainerIds.Remove(id); else p.ExcludedRetainerIds.Add(id); dirty = true; }
        }
        Ui.Hint("Unticked retainers are left alone.");
    }

    // ---------- Notifications ----------

    private void DrawNotifications()
    {
        var p = Editing;
        var dtr = p.ShowDtrEntry;
        if (Ui.Check("Show bag space in the server info bar", ref dtr)) { p.ShowDtrEntry = dtr; dirty = true; }
        Ui.Tooltip("Click it to open the review.");

        var pct = p.FullnessNudgePercent;
        ImGui.SetNextItemWidth(180 * Ui.Scale);
        if (ImGui.SliderInt("Nudge when bags are this full", ref pct, 50, 100, "%d%%", ImGuiSliderFlags.None)) { p.FullnessNudgePercent = pct; dirty = true; }

        var duty = p.PostDutyNudge;
        if (Ui.Check("Nudge after a duty when there is something to clean", ref duty)) { p.PostDutyNudge = duty; dirty = true; }

        var kinds = new[] { ContainerKind.Saddlebag, ContainerKind.Retainer, ContainerKind.GlamourDresser };
        var open = kinds.Any(p.IsAutoOpen);
        if (Ui.Check("Open the review when a saddlebag, retainer or dresser opens", ref open))
        {
            foreach (var k in kinds) p.AutoOpenOnContainer[k] = open;
            dirty = true;
        }
        Ui.Tooltip("Only when there is something to clean there.");
    }

    // ---------- Data ----------

    private void DrawIntegrations()
    {
        using (ImRaii.Disabled(!allagan.IsInstalled))
        {
            var alts = config.ShowAltSections;
            if (Ui.Check("Show other characters in the review", ref alts)) { config.ShowAltSections = alts; dirty = true; }
        }
        ImGui.SameLine();
        if (!allagan.IsInstalled) Ui.Pill("needs the Allagan Tools plugin", Ui.Warn);
        else Ui.Pill("Allagan Tools", Ui.Ok, Dalamud.Interface.FontAwesomeIcon.Check);

        Ui.Gap(0.4f);
        DrawDiscardHelperImport();
    }

    /// <summary>
    /// Brings across the lists from Discard Helper. Its file lives in a known place, so there is nothing to
    /// type: Gleam finds it, says what is in it, and only then offers to bring it in. Browsing is there for
    /// anyone whose game keeps its settings somewhere else.
    /// </summary>
    private void DrawDiscardHelperImport()
    {
        Ui.Hint("Share lists with Discard Helper");
        importFound ??= FindDiscardHelperFile();

        if (string.IsNullOrEmpty(importFound))
        {
            Ui.HintWrapped("Gleam could not find a Discard Helper configuration on this computer.");
            if (Ui.IconButton(Dalamud.Interface.FontAwesomeIcon.FolderOpen, "Find it myself")) BrowseForDiscardHelper();
            ImGui.SameLine();
            if (Ui.LinkButton("Send mine the other way")) SaveForDiscardHelper();
            if (!string.IsNullOrEmpty(importResult)) Ui.Hint(importResult);
            return;
        }

        var lists = importLists ??= ReadDiscardHelper(importFound);
        if (lists.IsEmpty)
        {
            Ui.HintWrapped($"Found {Path.GetFileName(importFound)}, but it has no items in either list yet.");
        }
        else
        {
            Ui.HintWrapped($"Found {lists.Discard.Count} item{(lists.Discard.Count == 1 ? "" : "s")} it throws away and {lists.Keep.Count} it protects.");
            if (Ui.PrimaryButton("Bring them in", 180 * Ui.Scale))
            {
                var junk = lists.Discard.Count(id => config.AlwaysDiscardList.Add(id, note: "From Discard Helper"));
                var kept = lists.Keep.Count(id => config.ProtectList.Add(id, note: "From Discard Helper"));
                importResult = $"Added {junk} to Always junk and {kept} to Keep these.";
                dirty = true;
            }
            Ui.Tooltip("What it threw away joins your Always junk list. What it protected joins Keep these.");
            ImGui.SameLine();
        }
        if (Ui.LinkButton("Use a different file")) BrowseForDiscardHelper();
        ImGui.SameLine();
        if (Ui.LinkButton("Send mine the other way")) SaveForDiscardHelper();
        Ui.Tooltip("Writes your Always junk and Keep these lists to a file Discard Helper can read.");
        if (!string.IsNullOrEmpty(importResult)) Ui.Hint(importResult);
    }

    /// <summary>Writes Gleam's two lists out in Discard Helper's shape.</summary>
    private void SaveForDiscardHelper()
    {
        var mine = new Core.Integrations.DiscardHelperLists(
            config.AlwaysDiscardList.Entries.Select(e => e.ItemId).Distinct().ToList(),
            config.ProtectList.Entries.Select(e => e.ItemId).Distinct().ToList());
        if (mine.IsEmpty) { importResult = "Both of your lists are empty, so there is nothing to send."; return; }

        FileDialogs.SaveFileDialog("Where should Gleam write it?", ".json", "ARDiscard.json", ".json", (ok, path) =>
        {
            if (!ok || string.IsNullOrEmpty(path)) return;
            try
            {
                File.WriteAllText(path, Core.Integrations.DiscardHelperImport.Write(mine));
                importResult = $"Wrote {mine.Discard.Count} to throw away and {mine.Keep.Count} to protect.";
            }
            catch (Exception ex)
            {
                importResult = $"Could not write it: {ex.Message}";
            }
        });
    }

    private void BrowseForDiscardHelper()
    {
        var start = Path.GetDirectoryName(importFound) ?? PluginServices.PluginInterface.GetPluginConfigDirectory();
        FileDialogs.OpenFileDialog("Find your Discard Helper file", ".json", (ok, paths) =>
        {
            if (!ok || paths.Count == 0) return;
            importFound = paths[0];
            importLists = ReadDiscardHelper(importFound);
            importResult = importLists.IsEmpty ? "That file has no Discard Helper lists in it." : string.Empty;
        }, 1, start, true);
    }

    private Core.Integrations.DiscardHelperLists ReadDiscardHelper(string path)
    {
        try
        {
            return Core.Integrations.DiscardHelperImport.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            importResult = $"Could not read it: {ex.Message}";
            return Core.Integrations.DiscardHelperLists.Empty;
        }
    }

    /// <summary>Discard Helper keeps its settings beside every other plugin's, so look there first.</summary>
    private static string? FindDiscardHelperFile()
    {
        var mine = PluginServices.PluginInterface.GetPluginConfigDirectory();
        var configs = Directory.GetParent(mine)?.FullName;
        if (configs is null) return string.Empty;
        foreach (var name in new[] { "ARDiscard.json", "DiscardHelper.json" })
        {
            var path = Path.Combine(configs, name);
            if (File.Exists(path)) return path;
        }
        return string.Empty;
    }
}
