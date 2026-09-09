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
            if (ImGui.Checkbox(rule.Name, ref on)) { if (on) p.EnabledRules.Add(rule.Id); else p.EnabledRules.Remove(rule.Id); dirty = true; }
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
            if (ImGui.Checkbox($"{name}##ret{id}", ref included)) { if (included) p.ExcludedRetainerIds.Remove(id); else p.ExcludedRetainerIds.Add(id); dirty = true; }
        }
        Ui.Hint("Unticked retainers are left alone.");
    }

    // ---------- Notifications ----------

    private void DrawNotifications()
    {
        var p = Editing;
        var dtr = p.ShowDtrEntry;
        if (ImGui.Checkbox("Show bag space in the server info bar", ref dtr)) { p.ShowDtrEntry = dtr; dirty = true; }
        Ui.Tooltip("Click it to open the review.");

        var pct = p.FullnessNudgePercent;
        ImGui.SetNextItemWidth(180 * Ui.Scale);
        if (ImGui.SliderInt("Nudge when bags are this full", ref pct, 50, 100, "%d%%", ImGuiSliderFlags.None)) { p.FullnessNudgePercent = pct; dirty = true; }

        var duty = p.PostDutyNudge;
        if (ImGui.Checkbox("Nudge after a duty when there is something to clean", ref duty)) { p.PostDutyNudge = duty; dirty = true; }

        var kinds = new[] { ContainerKind.Saddlebag, ContainerKind.Retainer, ContainerKind.GlamourDresser };
        var open = kinds.Any(p.IsAutoOpen);
        if (ImGui.Checkbox("Open the review when a saddlebag, retainer or dresser opens", ref open))
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
            if (ImGui.Checkbox("Show other characters in the review", ref alts)) { config.ShowAltSections = alts; dirty = true; }
        }
        ImGui.SameLine();
        if (!allagan.IsInstalled) Ui.Pill("needs the Allagan Tools plugin", Ui.Warn);
        else Ui.Pill("Allagan Tools", Ui.Ok, Dalamud.Interface.FontAwesomeIcon.Check);

        Ui.Gap(0.4f);
        Ui.Hint("Bring in a Discard Helper list");
        ImGui.SetNextItemWidth(-110 * Ui.Scale);
        Ui.InputText("##import", "Path to ARDiscard.json", ref importPath, 512);
        ImGui.SameLine();
        if (Ui.Button("Import", 96 * Ui.Scale))
        {
            try
            {
                var ids = Core.Integrations.DiscardHelperImport.ParseItemIds(File.ReadAllText(importPath));
                var added = ids.Count(id => config.AlwaysDiscardList.Add(id, note: "Imported from Discard Helper"));
                importResult = $"Added {added} item{(added == 1 ? "" : "s")} to Always clean.";
                dirty = true;
            }
            catch (Exception ex)
            {
                importResult = $"Could not import: {ex.Message}";
            }
        }
        if (!string.IsNullOrEmpty(importResult)) Ui.Hint(importResult);
    }
}
