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
        Ui.Section("Rules");
        var rulesLive = Override(nameof(Profile.EnabledRules));
        var overridesLive = !editingCharacter || config.Profiles.IsOverridden(player.ContentId, nameof(Profile.RuleActionOverrides));

        using (ImRaii.Disabled(!rulesLive))
        {
            foreach (var rule in RuleEngine.AllRules) DrawRuleRow(p, rule.Id, rule.Name, rule.Description, overridesLive, allowsAction: true);
            DrawRuleRow(p, MarketPricePostProcessor.RuleId, "Vendor vs market",
                "Re-routes rows: show-only when the market clearly beats the vendor, sell when a discard has vendor value. Needs Universalis.",
                overridesLive, allowsAction: false);
        }

        if (editingCharacter)
        {
            Ui.Gap(0.5f);
            Override(nameof(Profile.RuleActionOverrides));
            Ui.Hint("Override preferred actions for this character");
        }

        Ui.Section("Fine-tune");
        if (ImGui.CollapsingHeader("Thresholds", ImGuiTreeNodeFlags.None)) DrawThresholds();
    }

    private void DrawRuleRow(Profile p, string ruleId, string name, string description, bool overridesLive, bool allowsAction)
    {
        using var id = ImRaii.PushId(ruleId);
        var on = p.EnabledRules.Contains(ruleId);
        if (ImGui.Checkbox(name, ref on)) { if (on) p.EnabledRules.Add(ruleId); else p.EnabledRules.Remove(ruleId); dirty = true; }
        Ui.Tooltip(description);
        if (!allowsAction) return;

        ImGui.SameLine();
        Ui.RightAlign(150 * Ui.Scale);
        var options = new List<string> { "rule decides", ActionKind.Discard.Label(), ActionKind.VendorSell.Label(), ActionKind.ExpertDelivery.Label(), ActionKind.Desynth.Label() };
        var kinds = new[] { ActionKind.None, ActionKind.Discard, ActionKind.VendorSell, ActionKind.ExpertDelivery, ActionKind.Desynth };
        var idx = p.RuleActionOverrides.TryGetValue(ruleId, out var a) ? Array.IndexOf(kinds, a) : 0;
        if (idx < 0) idx = 0;
        ImGui.SetNextItemWidth(140 * Ui.Scale);
        using (ImRaii.Disabled(!overridesLive))
        using (ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Zero))
        {
            if (Ui.Combo("##action", ref idx, options))
            {
                if (idx == 0) p.RuleActionOverrides.Remove(ruleId); else p.RuleActionOverrides[ruleId] = kinds[idx];
                dirty = true;
            }
        }
        Ui.Tooltip("Preferred action, used when the rule allows it for that item.");
    }

    private void DrawThresholds()
    {
        var p = Editing;
        var live = Override(nameof(Profile.Thresholds));
        Ui.Hint($"Preset: {Presets.Detect(p.Thresholds)}. Any change here makes it custom.");
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
        Ui.Tooltip("Market wins when market × (1 − tax) exceeds vendor × this.");
        var minMk = t.MarketMinStackValueGil;
        ImGui.SetNextItemWidth(w);
        if (Ui.InputLong("Market: min stack value", ref minMk)) { t.MarketMinStackValueGil = minMk; c = true; }
        var tax = t.MarketTaxRate;
        ImGui.SetNextItemWidth(w);
        if (Ui.SliderDouble("Market: tax rate", ref tax, 0.0, 0.1, "%.2f")) { t.MarketTaxRate = tax; c = true; }

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
        Ui.Section("Open the review automatically");
        var ao = Override(nameof(Profile.AutoOpenOnContainer));
        using (ImRaii.Disabled(!ao))
        {
            foreach (var kind in new[] { ContainerKind.Saddlebag, ContainerKind.Retainer, ContainerKind.GlamourDresser })
            {
                var on = p.IsAutoOpen(kind);
                if (ImGui.Checkbox($"When the {kind.DisplayName().ToLowerInvariant()} opens##ao{kind}", ref on)) { p.AutoOpenOnContainer[kind] = on; dirty = true; }
            }
        }
        Ui.Hint("Only when there is something to clean there.");

        Ui.Section("Retainers");
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
        Toggle("Retainer sections start collapsed", nameof(Profile.RetainerSectionsCollapsed), p.RetainerSectionsCollapsed, v => p.RetainerSectionsCollapsed = v,
            "Venture rewards and market returns land in retainers; a collapsed section forces a deliberate look.");
    }

    // ---------- Automation ----------

    private void DrawAutomation()
    {
        var a = config.Automation;
        Ui.Section("Steps");
        var v = a.OpenSaddlebag; if (ImGui.Checkbox("Open the saddlebag", ref v)) { a.OpenSaddlebag = v; dirty = true; }
        v = a.TravelToInn; if (ImGui.Checkbox("Travel to an inn with Lifestream", ref v)) { a.TravelToInn = v; dirty = true; }
        Ui.Tooltip("Off: you must already be standing in an inn room.");
        v = a.VisitRetainers; if (ImGui.Checkbox("Visit each retainer at the bell", ref v)) { a.VisitRetainers = v; dirty = true; }
        v = a.SellAtVendor; if (ImGui.Checkbox("Sell vendor rows at a merchant NPC", ref v)) { a.SellAtVendor = v; dirty = true; }
        Ui.Tooltip("Retainers cannot buy items. After the other legs, the pilot looks for the named merchant nearby and sells there.");
        v = a.VisitDresser; if (ImGui.Checkbox("Visit the glamour dresser", ref v)) { a.VisitDresser = v; dirty = true; }
        v = a.VisitGrandCompany; if (ImGui.Checkbox("Visit your Grand Company for Expert Delivery", ref v)) { a.VisitGrandCompany = v; dirty = true; }
        Ui.Tooltip("Teleports to your GC's city, reaches the HQ, and talks to the personnel officer.");
        Ui.Gap(0.3f);
        Ui.Hint("Items found only once a container opens:");
        ImGui.SameLine();
        var unseen = a.UnseenRows;
        if (Ui.Segmented("##unseen", ref unseen, [(UnseenRowsMode.Clean, "Clean by the rules"), (UnseenRowsMode.Ask, "Ask me"), (UnseenRowsMode.Skip, "Skip")])) { a.UnseenRows = unseen; dirty = true; }
        Ui.Tooltip("Retainers not yet cached and the dresser only show their contents when open. Clean applies the preset and hard rules to them on the spot.");

        Ui.Section("Names in your client language");
        var w = 200 * Ui.Scale;
        var s = a.BellObjectName; ImGui.SetNextItemWidth(w); if (Ui.InputText("Summoning bell", "", ref s, 64)) { a.BellObjectName = s; dirty = true; }
        s = a.DresserObjectName; ImGui.SetNextItemWidth(w); if (Ui.InputText("Glamour dresser", "", ref s, 64)) { a.DresserObjectName = s; dirty = true; }
        s = a.EntrustMenuText; ImGui.SetNextItemWidth(w); if (Ui.InputText("Retainer menu: inventory", "", ref s, 64)) { a.EntrustMenuText = s; dirty = true; }
        s = a.VendorNpcName; ImGui.SetNextItemWidth(w); if (Ui.InputText("Merchant NPC", "", ref s, 64)) { a.VendorNpcName = s; dirty = true; }
        s = a.VendorMenuText; ImGui.SetNextItemWidth(w); if (Ui.InputText("Merchant menu: open shop", "", ref s, 64)) { a.VendorMenuText = s; dirty = true; }
        s = a.VendorAetheryte; ImGui.SetNextItemWidth(w); if (Ui.InputText("Merchant: teleport to", "", ref s, 64)) { a.VendorAetheryte = s; dirty = true; }
        Ui.Tooltip("Lifestream destination with a merchant right by the aetheryte. Used only when no merchant is within reach.");
        s = a.QuitMenuText; ImGui.SetNextItemWidth(w); if (Ui.InputText("Retainer menu: quit", "", ref s, 64)) { a.QuitMenuText = s; dirty = true; }
        s = a.PersonnelOfficerName; ImGui.SetNextItemWidth(w); if (Ui.InputText("GC personnel officer", "", ref s, 64)) { a.PersonnelOfficerName = s; dirty = true; }
        s = a.GcSupplyMenuText; ImGui.SetNextItemWidth(w); if (Ui.InputText("Officer menu: supply missions", "", ref s, 64)) { a.GcSupplyMenuText = s; dirty = true; }
        Ui.Hint("Menu texts are matched as substrings, case-insensitive.");

        Ui.Section("Grand Company route");
        foreach (var (id, label) in new (byte, string)[] { (1, "Maelstrom"), (2, "Twin Adder"), (3, "Immortal Flames") })
        {
            var city = a.GcCityAetheryte.GetValueOrDefault(id, string.Empty);
            var shard = a.GcAethernetShard.GetValueOrDefault(id, string.Empty);
            ImGui.SetNextItemWidth(w); if (Ui.InputText($"{label}: aetheryte", "", ref city, 64)) { a.GcCityAetheryte[id] = city; dirty = true; }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(w); if (Ui.InputText($"shard##{id}", "aethernet shard (optional)", ref shard, 64)) { a.GcAethernetShard[id] = shard; dirty = true; }
        }
        var tab = a.ExpertDeliveryTabCallback; ImGui.SetNextItemWidth(w); if (Ui.InputText("Expert Delivery tab callback", "", ref tab, 32)) { a.ExpertDeliveryTabCallback = tab; dirty = true; }
        Ui.Tooltip("Comma-separated ints fired on the supply window to switch to the Expert Delivery tab.");

        Ui.Section("Timing");
        var t = a.TravelTimeoutSeconds; ImGui.SetNextItemWidth(100 * Ui.Scale); if (Ui.InputInt("Travel timeout (s)", ref t, 10)) { a.TravelTimeoutSeconds = Math.Clamp(t, 30, 600); dirty = true; }
        t = a.StepTimeoutSeconds; ImGui.SetNextItemWidth(100 * Ui.Scale); if (Ui.InputInt("Menu step timeout (s)", ref t, 5)) { a.StepTimeoutSeconds = Math.Clamp(t, 5, 120); dirty = true; }
        var r = a.InteractRange; ImGui.SetNextItemWidth(100 * Ui.Scale); if (ImGui.SliderFloat("Interact range (yalms)", ref r, 2f, 6f, "%.1f", ImGuiSliderFlags.None)) { a.InteractRange = r; dirty = true; }
        var sel = a.RetainerListSelect; ImGui.SetNextItemWidth(100 * Ui.Scale); if (Ui.InputInt("Retainer list callback", ref sel)) { a.RetainerListSelect = sel; dirty = true; }
        Ui.Tooltip("First callback value used to pick a retainer from the list. 2 is the known value; change only if selection fails.");
    }

    // ---------- Notifications ----------

    private void DrawNotifications()
    {
        var p = Editing;
        Ui.Section("Server info bar");
        Toggle("Show used slots and cleanable count", nameof(Profile.ShowDtrEntry), p.ShowDtrEntry, v => p.ShowDtrEntry = v, "Click it to open the review.");
        var np = Override(nameof(Profile.FullnessNudgePercent));
        using (ImRaii.Disabled(!np))
        {
            var pct = p.FullnessNudgePercent;
            ImGui.SetNextItemWidth(180 * Ui.Scale);
            if (ImGui.SliderInt("Toast when inventory is this full", ref pct, 50, 100, "%d%%", ImGuiSliderFlags.None)) { p.FullnessNudgePercent = pct; dirty = true; }
        }

        Ui.Section("After duties");
        Toggle("Toast how many items could be cleaned", nameof(Profile.PostDutyNudge), p.PostDutyNudge, v => p.PostDutyNudge = v,
            "A few seconds after a duty ends, once loot has landed.");
    }

    // ---------- Integrations ----------

    private void DrawIntegrations()
    {
        Ui.Section("Universalis");
        var uni = config.UseUniversalis;
        if (ImGui.Checkbox("Use market prices", ref uni)) { config.UseUniversalis = uni; dirty = true; }
        Ui.Tooltip("Cached 15 minutes, fetched 100 items at a time. A failed fetch just means \"market unknown\".");

        Ui.Section("Allagan Tools");
        var at = config.UseAllaganTools;
        if (ImGui.Checkbox("Read closed containers and other characters", ref at)) { config.UseAllaganTools = at; allagan.Enabled = at; dirty = true; }
        ImGui.SameLine();
        if (!allagan.IsInstalled) Ui.Pill("not installed", Ui.Muted);
        else if (allagan.IsAvailable) Ui.Pill("connected", Ui.Ok);
        else Ui.Pill("starting", Ui.Warn);
        var alts = config.ShowAltSections;
        if (ImGui.Checkbox("Show other characters in the review", ref alts)) { config.ShowAltSections = alts; dirty = true; }

        Ui.Section("Import from Discard Helper");
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
        Ui.Section("Timing");
        var rl = cb.RateLimitMs;
        ImGui.SetNextItemWidth(120 * Ui.Scale);
        if (Ui.InputInt("Pause between actions (ms)", ref rl, 50)) { cb.RateLimitMs = Math.Clamp(rl, 100, 5000); dirty = true; }
        var to = cb.ActionTimeoutMs;
        ImGui.SetNextItemWidth(120 * Ui.Scale);
        if (Ui.InputInt("Give up on an action after (ms)", ref to, 500)) { cb.ActionTimeoutMs = Math.Clamp(to, 1000, 30000); dirty = true; }

        Ui.Section("Verification");
        ImGui.SameLine(0, 0);
        if (Ui.Button("Open the spike window")) openDebug();
        ImGui.SameLine();
        if (config.SpikesVerified) Ui.Pill("verified", Ui.Ok); else Ui.Pill("not verified", Ui.Warn);
        Ui.Hint("Dialog button values and menu labels live in the spike window. Change them only if a spike shows a default is wrong.");

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
