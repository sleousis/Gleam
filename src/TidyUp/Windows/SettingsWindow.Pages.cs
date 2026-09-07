using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using TidyUp.Core.Lists;
using TidyUp.Core.Model;
using TidyUp.Core.Rules;

namespace TidyUp.Windows;

public sealed partial class SettingsWindow
{
    private void Toggle(string label, string prop, bool value, Action<bool> set, string? hint = null)
    {
        var live = Override(prop);
        using var d = ImRaii.Disabled(!live);
        if (ImGui.Checkbox(label, ref value)) { set(value); dirty = true; }
        if (hint is not null) Ui.Tooltip(hint);
    }

    // ---------- Rules ----------

    private void DrawRules()
    {
        var p = Editing;
        var rulesLive = Override(nameof(Profile.EnabledRules));
        using (ImRaii.Disabled(!rulesLive))
        {
            foreach (var rule in RuleEngine.AllRules) DrawRuleRow(p, rule.Id, rule.Name, rule.Description);
            DrawRuleRow(p, MarketPricePostProcessor.RuleId, "Warn when the market pays far more", "Flags rows whose market value clearly beats the vendor price, so they start unticked.");
        }
        Ui.Hint("The preset decides what happens to what the rules find; each row can still be changed by hand.");

        Ui.Gap(0.5f);
        if (ImGui.CollapsingHeader("Thresholds", ImGuiTreeNodeFlags.None)) DrawThresholds();
    }

    private void DrawRuleRow(Profile p, string ruleId, string name, string description)
    {
        using var id = ImRaii.PushId(ruleId);
        var on = p.EnabledRules.Contains(ruleId);
        if (ImGui.Checkbox(name, ref on)) { if (on) p.EnabledRules.Add(ruleId); else p.EnabledRules.Remove(ruleId); dirty = true; }
        Ui.Tooltip(description);
    }

    private void DrawThresholds()
    {
        var p = Editing;
        var live = Override(nameof(Profile.Thresholds));
        Ui.Hint($"Preset: {Presets.Detect(p.Thresholds).Label()}. Any change here makes it custom.");
        using var dis = ImRaii.Disabled(!live);
        var t = p.Thresholds;
        var c = false;
        var w = 120 * Ui.Scale;

        Ui.Gap(0.5f);
        c |= Number("Gear: level gap", ref t, x => x.ObsoleteGearLevelGap, (x, v) => x.ObsoleteGearLevelGap = v, w,
            "Gear is obsolete when the best job that can wear it is this many levels above the gear's equip level.");
        var unplayed = t.IncludeGearForUnplayedJobs;
        if (ImGui.Checkbox("Gear: include jobs never levelled", ref unplayed)) { t.IncludeGearForUnplayedJobs = unplayed; c = true; }
        c |= Number("Consumables: item level gap", ref t, x => x.ConsumableItemLevelGap, (x, v) => x.ConsumableItemLevelGap = v, w,
            "Food and medicine are outleveled when their item level is this far below your best gearset.");
        c |= Number("Materials: max recipe level", ref t, x => x.CraftingMatMaxRecipeLevel, (x, v) => x.CraftingMatMaxRecipeLevel = v, w,
            "A material is unusable only if every recipe using it is at or below this level…");
        c |= Number("Materials: crafter lead", ref t, x => x.CraftingMatCrafterLeadLevels, (x, v) => x.CraftingMatCrafterLeadLevels = v, w,
            "…and the crafter is this many levels past each of those recipes.");

        var vp = t.VendorOnlyMaxUnitPrice;
        ImGui.SetNextItemWidth(w);
        if (Ui.InputUInt("Vendor junk: max unit price", ref vp)) { t.VendorOnlyMaxUnitPrice = vp; c = true; }
        Ui.Tooltip("Vendor-only items worth more than this each are left alone.");

        var factor = t.MarketPremiumFactor;
        ImGui.SetNextItemWidth(w);
        if (Ui.SliderDouble("Market: premium factor", ref factor, 1.0, 5.0, "%.1fx")) { t.MarketPremiumFactor = factor; c = true; }
        Ui.Tooltip("Warn when the market value, after tax, is more than this many times the vendor price.");
        var minMk = t.MarketMinStackValueGil;
        ImGui.SetNextItemWidth(w);
        if (Ui.InputLong("Market: ignore stacks under", ref minMk)) { t.MarketMinStackValueGil = minMk; c = true; }
        Ui.Tooltip("Stacks worth less than this on the market are not worth the warning.");

        c |= Number("Cap: items per run", ref t, x => x.SoftCapItems, (x, v) => x.SoftCapItems = v, w,
            "Beyond this, the Clean button needs a second click.");
        var capGil = t.SoftCapGil;
        ImGui.SetNextItemWidth(w);
        if (Ui.InputLong("Cap: gil at risk per run", ref capGil, 5000)) { t.SoftCapGil = capGil; c = true; }
        Ui.Tooltip("Vendor value destroyed or sold in one run. Whichever cap trips first counts.");

        if (c) { p.Preset = Presets.Detect(t); dirty = true; }
    }

    private static bool Number(string label, ref Thresholds t, Func<Thresholds, int> get, Action<Thresholds, int> set, float width, string help)
    {
        var v = get(t);
        ImGui.SetNextItemWidth(width);
        var changed = Ui.InputInt(label, ref v);
        if (changed) set(t, Math.Max(0, v));
        Ui.Tooltip(help);
        return changed;
    }

    // ---------- Containers ----------

    private void DrawContainers()
    {
        var p = Editing;
        var ex = Override(nameof(Profile.ExcludedRetainerIds));
        using (ImRaii.Disabled(!ex))
        {
            var known = coordinator.RetainerNames;
            if (known.Count == 0) Ui.Hint("Summon a retainer once so Tidy Up learns their names.");
            foreach (var (id, name) in known)
            {
                var included = !p.ExcludedRetainerIds.Contains(id);
                if (ImGui.Checkbox($"{name}##ret{id}", ref included)) { if (included) p.ExcludedRetainerIds.Remove(id); else p.ExcludedRetainerIds.Add(id); dirty = true; }
            }
        }
        Ui.Hint("Unticked retainers are left alone entirely.");

        Ui.Gap(0.5f);
        var ao = Override(nameof(Profile.AutoOpenOnContainer));
        using (ImRaii.Disabled(!ao))
        {
            var kinds = new[] { ContainerKind.Saddlebag, ContainerKind.Retainer, ContainerKind.GlamourDresser };
            var on = kinds.Any(p.IsAutoOpen);
            if (ImGui.Checkbox("Open the review when a saddlebag, retainer or dresser opens", ref on))
            {
                foreach (var k in kinds) p.AutoOpenOnContainer[k] = on;
                dirty = true;
            }
            Ui.Tooltip("Only when there is something to clean there. Off while a hands-free run is going.");
        }
    }

    // ---------- Automation ----------

    private void DrawAutomation()
    {
        var a = config.Automation;
        Ui.Section("Where a run goes");
        var v = a.OpenSaddlebag; if (ImGui.Checkbox("Saddlebag", ref v)) { a.OpenSaddlebag = v; dirty = true; }
        v = a.VisitRetainers; if (ImGui.Checkbox("Retainers, at an inn bell", ref v)) { a.VisitRetainers = v; dirty = true; }
        v = a.VisitDresser; if (ImGui.Checkbox("Glamour dresser", ref v)) { a.VisitDresser = v; dirty = true; }
        v = a.SellAtVendor; if (ImGui.Checkbox("A merchant, to sell", ref v)) { a.SellAtVendor = v; dirty = true; }
        v = a.VisitGrandCompany; if (ImGui.Checkbox("Your Grand Company, for Expert Delivery", ref v)) { a.VisitGrandCompany = v; dirty = true; }
        Ui.Hint("A run only travels where ticked rows are.");

        Ui.Section("Behaviour");
        var cleanUnseen = a.UnseenRows != UnseenRowsMode.Skip;
        if (ImGui.Checkbox("Clean items discovered when a container opens", ref cleanUnseen)) { a.UnseenRows = cleanUnseen ? UnseenRowsMode.Clean : UnseenRowsMode.Skip; dirty = true; }
        Ui.Tooltip("Retainers not yet cached and the dresser only show their contents once open. On: the preset is applied to them on the spot. Off: they wait for the next review.");
        v = a.VisitContainersWithoutRows; if (ImGui.Checkbox("Also visit containers with nothing ticked", ref v)) { a.VisitContainersWithoutRows = v; dirty = true; }
        Ui.Tooltip("Looks inside every enabled container on every run, even when the list had nothing for it.");

        Ui.Section("Merchant");
        var w = 220 * Ui.Scale;
        var s = a.VendorAetheryte; ImGui.SetNextItemWidth(w); if (Ui.InputText("Teleport to", "", ref s, 64)) { a.VendorAetheryte = s; dirty = true; }
        Ui.Tooltip("An aetheryte with a merchant right next to it. Used when no merchant is in reach.");

        Ui.Section("Patience");
        var t = a.TravelTimeoutSeconds; ImGui.SetNextItemWidth(100 * Ui.Scale); if (Ui.InputInt("Travel (s)", ref t, 10)) { a.TravelTimeoutSeconds = Math.Clamp(t, 30, 600); dirty = true; }
        t = a.StepTimeoutSeconds; ImGui.SetNextItemWidth(100 * Ui.Scale); if (Ui.InputInt("Each menu step (s)", ref t, 5)) { a.StepTimeoutSeconds = Math.Clamp(t, 5, 120); dirty = true; }
        Ui.Hint("Playing in another language? The names Tidy Up looks for can be changed in Troubleshooting.");
    }

    // ---------- Notifications ----------

    private void DrawNotifications()
    {
        var p = Editing;
        Toggle("Show bag usage in the server info bar", nameof(Profile.ShowDtrEntry), p.ShowDtrEntry, v => p.ShowDtrEntry = v, "Click it to open the review.");
        var np = Override(nameof(Profile.FullnessNudgePercent));
        using (ImRaii.Disabled(!np))
        {
            var pct = p.FullnessNudgePercent;
            ImGui.SetNextItemWidth(180 * Ui.Scale);
            if (ImGui.SliderInt("Nudge when bags are this full", ref pct, 50, 100, "%d%%", ImGuiSliderFlags.None)) { p.FullnessNudgePercent = pct; dirty = true; }
        }
        Toggle("Nudge after a duty when there is something to clean", nameof(Profile.PostDutyNudge), p.PostDutyNudge, v => p.PostDutyNudge = v,
            "A few seconds after a duty ends, once loot has landed.");
    }

    // ---------- Integrations ----------

    private void DrawIntegrations()
    {
        var uni = config.UseUniversalis;
        if (ImGui.Checkbox("Market prices from Universalis", ref uni)) { config.UseUniversalis = uni; dirty = true; }
        Ui.Tooltip("Lowest listing on your home world, refreshed every 15 minutes. Without it, market rows fall back to the vendor.");

        var at = config.UseAllaganTools;
        if (ImGui.Checkbox("Closed containers from Allagan Tools", ref at)) { config.UseAllaganTools = at; allagan.Enabled = at; dirty = true; }
        ImGui.SameLine();
        if (!allagan.IsInstalled) Ui.Pill("not installed", Ui.Muted);
        else if (allagan.IsAvailable) Ui.Pill("connected", Ui.Ok);
        else Ui.Pill("starting", Ui.Warn);
        var alts = config.ShowAltSections;
        if (ImGui.Checkbox("Show other characters in the review", ref alts)) { config.ShowAltSections = alts; dirty = true; }

        Ui.Section("Import a Discard Helper list");
        ImGui.SetNextItemWidth(-120 * Ui.Scale);
        Ui.InputText("##import", "Path to ARDiscard.json", ref importPath, 512);
        ImGui.SameLine();
        if (Ui.Button("Import", 100 * Ui.Scale))
        {
            try
            {
                var ids = Core.Integrations.DiscardHelperImport.ParseItemIds(File.ReadAllText(importPath));
                var added = ids.Count(id => config.AlwaysDiscardList.Add(id, note: "Imported from Discard Helper"));
                importResult = $"Added {added} items to the always-discard list ({ids.Count} found).";
                dirty = true;
            }
            catch (Exception ex)
            {
                importResult = $"Could not import: {ex.Message}";
            }
        }
        if (!string.IsNullOrEmpty(importResult)) Ui.Hint(importResult);
    }

    // ---------- Advanced ----------

    private void DrawAdvanced()
    {
        var cb = config.Callbacks;
        Ui.Section("Pace");
        var rl = cb.RateLimitMs;
        ImGui.SetNextItemWidth(120 * Ui.Scale);
        if (Ui.InputInt("Pause between actions (ms)", ref rl, 50)) { cb.RateLimitMs = Math.Clamp(rl, 100, 5000); dirty = true; }
        var to = cb.ActionTimeoutMs;
        ImGui.SetNextItemWidth(120 * Ui.Scale);
        if (Ui.InputInt("Give up on an action after (ms)", ref to, 500)) { cb.ActionTimeoutMs = Math.Clamp(to, 1000, 30000); dirty = true; }

        Ui.Section("Materia");
        var act = config.ActWhenMateriaFails;
        if (ImGui.Checkbox("If materia cannot be retrieved, act anyway and lose it", ref act)) { config.ActWhenMateriaFails = act; dirty = true; }
        Ui.Tooltip("Off: the item is left in place with the reason, so you can remove the materia by hand. Retrieval needs the materia-retrieval quest and free bag slots.");

        Ui.Section("Troubleshooting");
        if (Ui.Button("Open troubleshooting")) openDebug();
        Ui.Hint("Only needed if a step keeps failing after a game update.");

        Ui.Section("Per-character overrides");
        if (config.Profiles.Overrides.Count == 0) Ui.Hint("None.");
        foreach (var o in config.Profiles.Overrides.ToList())
        {
            Ui.Text($"{o.CharacterName}");
            ImGui.SameLine();
            Ui.Hint($"{o.OverriddenProperties.Count} overridden");
            ImGui.SameLine();
            if (Ui.LinkButton($"Remove##{o.CharacterId}")) { config.Profiles.Overrides.Remove(o); dirty = true; }
        }
    }
}
