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
        {
            using var id = ImRaii.PushId(MarketPricePostProcessor.RuleId);
            var on = p.EnabledRules.Contains(MarketPricePostProcessor.RuleId);
            if (ImGui.Checkbox("Warn when the market pays far more than a vendor", ref on))
            {
                if (on) p.EnabledRules.Add(MarketPricePostProcessor.RuleId); else p.EnabledRules.Remove(MarketPricePostProcessor.RuleId);
                dirty = true;
            }
            Ui.Tooltip("Such items start unticked so you can decide.");
        }

        Ui.Gap(0.4f);
        var act = config.ActWhenMateriaFails;
        if (ImGui.Checkbox("If materia cannot be removed, clean the item anyway", ref act)) { config.ActWhenMateriaFails = act; dirty = true; }
        Ui.Tooltip("Off: the item is left alone and the list tells you why.");
    }

    // ---------- Hands-free ----------

    private void DrawAutomation()
    {
        var a = config.Automation;
        Ui.Hint("Where a run may go");
        var v = a.OpenSaddlebag; if (ImGui.Checkbox("Chocobo saddlebag", ref v)) { a.OpenSaddlebag = v; dirty = true; }
        v = a.VisitRetainers; if (ImGui.Checkbox("Retainers, at an inn", ref v)) { a.VisitRetainers = v; dirty = true; }
        v = a.VisitDresser; if (ImGui.Checkbox("Glamour dresser", ref v)) { a.VisitDresser = v; dirty = true; }
        v = a.SellAtVendor; if (ImGui.Checkbox("A merchant, to sell", ref v)) { a.SellAtVendor = v; dirty = true; }
        v = a.VisitGrandCompany; if (ImGui.Checkbox("Your Grand Company, for Expert Delivery", ref v)) { a.VisitGrandCompany = v; dirty = true; }

        Ui.Gap(0.4f);
        var cleanUnseen = a.UnseenRows != UnseenRowsMode.Skip;
        if (ImGui.Checkbox("Clean items discovered along the way", ref cleanUnseen)) { a.UnseenRows = cleanUnseen ? UnseenRowsMode.Clean : UnseenRowsMode.Skip; dirty = true; }
        Ui.Tooltip("Retainers and the dresser only show their contents once open. On: they are cleaned by the same rules on the spot. Off: they wait for your next review.");
        v = a.VisitContainersWithoutRows; if (ImGui.Checkbox("Look inside every container, even with nothing ticked", ref v)) { a.VisitContainersWithoutRows = v; dirty = true; }

        Ui.Gap(0.4f);
        var s = a.VendorAetheryte;
        ImGui.SetNextItemWidth(220 * Ui.Scale);
        if (Ui.InputText("Merchant to teleport to", "", ref s, 64)) { a.VendorAetheryte = s; dirty = true; }
        Ui.Tooltip("An aetheryte with a merchant right beside it, used when none is in reach.");
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
        var uni = config.UseUniversalis;
        if (ImGui.Checkbox("Show market board prices", ref uni)) { config.UseUniversalis = uni; dirty = true; }
        Ui.Tooltip("Lowest listing on your home world, from Universalis. Needed for the Sell on marketboard preset.");

        var at = config.UseAllaganTools;
        if (ImGui.Checkbox("Include closed containers and other characters", ref at)) { config.UseAllaganTools = at; allagan.Enabled = at; dirty = true; }
        ImGui.SameLine();
        if (!allagan.IsInstalled) Ui.Pill("needs the Allagan Tools plugin", Ui.Warn);
        else if (allagan.IsAvailable) Ui.Pill("Allagan Tools", Ui.Ok, Dalamud.Interface.FontAwesomeIcon.Check);
        else Ui.Pill("Allagan Tools starting", Ui.Warn);
        using (ImRaii.Disabled(!at))
        {
            var alts = config.ShowAltSections;
            if (ImGui.Checkbox("Show other characters in the review", ref alts)) { config.ShowAltSections = alts; dirty = true; }
        }

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
