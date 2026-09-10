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
        Ui.HintWrapped("Each line is one reason Gleam calls something junk. Untick one and it stops looking for that.");
        Ui.Gap(0.3f);
        foreach (var rule in RuleEngine.AllRules)
        {
            using var id = ImRaii.PushId(rule.Id);
            var on = p.EnabledRules.Contains(rule.Id);
            if (Ui.Check("##on", ref on)) { if (on) p.EnabledRules.Add(rule.Id); else p.EnabledRules.Remove(rule.Id); dirty = true; }
            ImGui.SameLine(0, 6 * Ui.Scale);
            ImGui.AlignTextToFramePadding();
            Ui.Icon(Ui.RuleIcon(rule.Id), on ? Ui.AccentSoft : Ui.Muted);
            ImGui.SameLine(0, 8 * Ui.Scale);
            Ui.Text(rule.Name);
            Ui.Tooltip(rule.Description);
        }

        Ui.Gap(0.4f);
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
        Ui.HintWrapped("An unticked retainer is left completely alone: nothing is cleaned from it and nothing is moved to it.");
        Ui.Gap(0.3f);
        if (known.Count == 0) { Ui.Hint("No retainers yet. They appear once you are logged in on a character that has some."); return; }
        foreach (var (id, name) in known)
        {
            var included = !p.ExcludedRetainerIds.Contains(id);
            if (Ui.Check($"{name}##ret{id}", ref included)) { if (included) p.ExcludedRetainerIds.Remove(id); else p.ExcludedRetainerIds.Add(id); dirty = true; }
        }
    }

    // ---------- Notifications ----------

    private void DrawNotifications()
    {
        var p = Editing;
        Ui.HintWrapped("Gleam never acts on its own. These only decide when it mentions that there is something to do.");
        Ui.Gap(0.3f);
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
        Ui.HintWrapped("Extras for plugins you may already have. Gleam works without every one of them.");
        Ui.Gap(0.3f);
        using (ImRaii.Disabled(!allagan.IsInstalled))
        {
            var alts = config.ShowAltSections;
            if (Ui.Check("Show other characters in the review", ref alts)) { config.ShowAltSections = alts; dirty = true; }
        }
        ImGui.SameLine();
        if (!allagan.IsInstalled) Ui.Pill("not installed", Ui.Warn, null, "req:Allagan");
        else Ui.Pill("Allagan Tools", Ui.Ok, Dalamud.Interface.FontAwesomeIcon.Check, "req:Allagan");

        Ui.Gap(0.4f);
        DrawDiscardHelperImport();
    }

    /// <summary>
    /// Two lists can come across from Discard Helper, and go back. Almost nobody needs this, so it is one
    /// line and one button: what is in the file, and whether to take it. Everything else is a quiet link.
    /// </summary>
    private void DrawDiscardHelperImport()
    {
        Ui.Hint("Discard Helper");
        importFound ??= FindDiscardHelperFile();
        var lists = string.IsNullOrEmpty(importFound) ? null : importLists ??= ReadDiscardHelper(importFound);
        var count = (lists?.Discard.Count ?? 0) + (lists?.Keep.Count ?? 0);

        if (count > 0)
        {
            if (Ui.PrimaryButton($"Take its {count} item{(count == 1 ? "" : "s")}", 200 * Ui.Scale))
            {
                var junk = lists!.Discard.Count(id => config.AlwaysDiscardList.Add(id, note: "From Discard Helper"));
                var kept = lists.Keep.Count(id => config.ProtectList.Add(id, note: "From Discard Helper"));
                ImportResult = junk + kept == 0 ? "You already have all of them." : $"Added {junk + kept}.";
                dirty = true;
            }
            Ui.Tooltip("What it throws away joins your always-junk list. What it protects joins your never-touch list.");
        }
        else
        {
            Ui.Hint(lists is null ? "Nothing found on this computer." : "Its lists are empty.");
        }

        ImGui.SameLine();
        if (Ui.LinkButton("Give it mine")) SaveForDiscardHelper();
        Ui.Tooltip("Writes your two lists to a file Discard Helper can read.");
        ImGui.SameLine();
        if (Ui.LinkButton("Pick a file")) BrowseForDiscardHelper();
        Ui.Tooltip(string.IsNullOrEmpty(importFound) ? "Find its settings file yourself." : $"Currently reading {Path.GetFileName(importFound)}.");

        // The result has its say and then fades, rather than sitting beside the links for the rest of the session.
        var resultLeft = (importResultUntil - DateTime.UtcNow).TotalSeconds;
        if (!string.IsNullOrEmpty(ImportResult) && resultLeft > 0)
        {
            ImGui.SameLine();
            using var fade = ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * (float)Math.Clamp(resultLeft / 0.6, 0, 1));
            Ui.TextSwap("ImportResult", ImportResult, Ui.Muted * new Vector4(1, 1, 1, 0.8f));
        }
    }

    /// <summary>Writes Gleam's two lists out in Discard Helper's shape.</summary>
    private void SaveForDiscardHelper()
    {
        var mine = new Core.Integrations.DiscardHelperLists(
            config.AlwaysDiscardList.Entries.Select(e => e.ItemId).Distinct().ToList(),
            config.ProtectList.Entries.Select(e => e.ItemId).Distinct().ToList());
        if (mine.IsEmpty) { ImportResult = "Your lists are empty."; return; }

        FileDialogs.SaveFileDialog("Where should Gleam write it?", ".json", "ARDiscard.json", ".json", (ok, path) =>
        {
            if (!ok || string.IsNullOrEmpty(path)) return;
            try
            {
                File.WriteAllText(path, Core.Integrations.DiscardHelperImport.Write(mine));
                ImportResult = $"Wrote {mine.Discard.Count + mine.Keep.Count}.";
            }
            catch (Exception)
            {
                ImportResult = "Could not write there.";
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
            ImportResult = importLists.IsEmpty ? "No lists in that file." : string.Empty;
        }, 1, start, true);
    }

    private Core.Integrations.DiscardHelperLists ReadDiscardHelper(string path)
    {
        try
        {
            return Core.Integrations.DiscardHelperImport.Parse(File.ReadAllText(path));
        }
        catch (Exception)
        {
            ImportResult = "Could not read that file.";
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
