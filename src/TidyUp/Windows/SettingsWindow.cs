using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using TidyUp.Core.Lists;
using TidyUp.Core.Model;
using TidyUp.Core.Rules;
using TidyUp.Game;
using TidyUp.Integrations;
using TidyUp.Services;

namespace TidyUp.Windows;

/// <summary>Every knob. Edits the account profile by default, or a character's overrides when that mode is on.</summary>
public sealed class SettingsWindow : Window
{
    private readonly Configuration config;
    private readonly IPlayerState player;
    private readonly ItemDatabase db;
    private readonly IconCache icons;
    private readonly AllaganToolsSource allagan;
    private readonly RunCoordinator coordinator;
    private readonly Action openDebug;
    private readonly ListEditor protectEditor;
    private readonly ListEditor alwaysEditor;

    private bool editingCharacter;
    private bool dirty;
    private string importPath = string.Empty;
    private string importResult = string.Empty;

    public SettingsWindow(Configuration config, IPlayerState player, ItemDatabase db, IconCache icons, AllaganToolsSource allagan, RunCoordinator coordinator, Action openDebug)
        : base("Tidy Up Settings###TidyUpSettings")
    {
        this.config = config;
        this.player = player;
        this.db = db;
        this.icons = icons;
        this.allagan = allagan;
        this.coordinator = coordinator;
        this.openDebug = openDebug;
        protectEditor = new ListEditor(db, icons, () => config.ProtectList, "Protect list", "Never proposed, whatever the rules say. Wins every conflict.", () => player.ContentId, MarkDirty);
        alwaysEditor = new ListEditor(db, icons, () => config.AlwaysDiscardList, "Always-discard list", "Proposed on every run without asking the rules. The protect list and the hard blocks still win.", () => player.ContentId, MarkDirty);
        Size = new Vector2(820, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    private void MarkDirty() => dirty = true;

    /// <summary>The profile the controls edit right now.</summary>
    private Profile Editing => editingCharacter
        ? config.Profiles.GetOrCreateOverride(player.ContentId, player.CharacterName).Values
        : config.Profiles.Account;

    private bool Overridden(string prop) => editingCharacter && config.Profiles.IsOverridden(player.ContentId, prop);

    /// <summary>Draws the per-character override toggle next to a setting; returns whether the control should be enabled.</summary>
    private bool OverrideToggle(string prop)
    {
        if (!editingCharacter) return true;
        var on = config.Profiles.IsOverridden(player.ContentId, prop);
        if (ImGui.Checkbox($"##ov{prop}", ref on))
        {
            config.Profiles.SetOverridden(player.ContentId, player.CharacterName, prop, on);
            dirty = true;
        }
        Ui.Tooltip(on ? "Overridden for this character" : "Using the account value. Tick to override for this character.");
        ImGui.SameLine();
        return on;
    }

    public override void Draw()
    {
        DrawModeBar();
        using var tabs = ImRaii.TabBar("##tabs");
        if (!tabs) return;
        Tab("Rules", DrawRules);
        Tab("Thresholds", DrawThresholds);
        Tab("Lists", DrawLists);
        Tab("Containers", DrawContainers);
        Tab("Notifications", DrawNotifications);
        Tab("Integrations", DrawIntegrations);
        Tab("Advanced", DrawAdvanced);

        if (dirty)
        {
            dirty = false;
            config.Save(PluginServices.PluginInterface);
        }
    }

    private static void Tab(string name, Action body)
    {
        using var tab = ImRaii.TabItem(name);
        if (!tab) return;
        using var child = ImRaii.Child($"##{name}", new Vector2(0, 0), false, ImGuiWindowFlags.None);
        if (child) body();
    }

    private void DrawModeBar()
    {
        var chr = player.IsLoaded ? player.CharacterName : "(not logged in)";
        Ui.Text("Editing:");
        ImGui.SameLine();
        if (ImGui.RadioButton("Account defaults", !editingCharacter)) editingCharacter = false;
        ImGui.SameLine();
        using (ImRaii.Disabled(!player.IsLoaded))
        {
            if (ImGui.RadioButton($"This character ({chr})", editingCharacter)) editingCharacter = true;
        }
        if (editingCharacter) Ui.Muted2("Tick the box beside a setting to override it for this character. Unticked settings follow the account.");
        ImGui.Separator();
    }

    // ---------- rules ----------

    private void DrawRules()
    {
        var p = Editing;
        Ui.Header("Preset");
        var enabled = OverrideToggle(nameof(Profile.Thresholds));
        using (ImRaii.Disabled(!enabled))
        {
            var preset = Presets.Detect(p.Thresholds);
            ImGui.SetNextItemWidth(160 * Ui.Scale);
            if (Ui.ComboEnum("##preset", ref preset) && preset != PresetName.Custom) { p.ApplyPreset(preset); dirty = true; }
            Ui.HelpMarker("Cautious proposes less and caps runs lower. Aggressive proposes more. Any manual change in Thresholds makes it Custom.");
        }

        Ui.Header("Rules");
        var rulesEnabled = OverrideToggle(nameof(Profile.EnabledRules));
        var overridesEnabled = editingCharacter ? config.Profiles.IsOverridden(player.ContentId, nameof(Profile.RuleActionOverrides)) : true;
        using (ImRaii.Disabled(!rulesEnabled))
        {
            using var table = ImRaii.Table("##rules", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.NoSavedSettings);
            if (table)
            {
                ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 30 * Ui.Scale, 0);
                ImGui.TableSetupColumn("Rule", ImGuiTableColumnFlags.WidthStretch, 3f, 0);
                ImGui.TableSetupColumn("Preferred action", ImGuiTableColumnFlags.WidthFixed, 170 * Ui.Scale, 0);
                ImGui.TableHeadersRow();
                foreach (var rule in RuleEngine.AllRules)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    var on = p.EnabledRules.Contains(rule.Id);
                    if (ImGui.Checkbox($"##{rule.Id}", ref on)) { if (on) p.EnabledRules.Add(rule.Id); else p.EnabledRules.Remove(rule.Id); dirty = true; }
                    ImGui.TableNextColumn();
                    Ui.Text(rule.Name);
                    Ui.Muted2(rule.Description);
                    Ui.Muted2($"Applies to: {string.Join(", ", rule.Containers.Select(c => c.DisplayName()))}");
                    ImGui.TableNextColumn();
                    DrawActionOverride(p, rule.Id, overridesEnabled);
                }
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var mk = p.EnabledRules.Contains(MarketPricePostProcessor.RuleId);
                if (ImGui.Checkbox("##mk", ref mk)) { if (mk) p.EnabledRules.Add(MarketPricePostProcessor.RuleId); else p.EnabledRules.Remove(MarketPricePostProcessor.RuleId); dirty = true; }
                ImGui.TableNextColumn();
                Ui.Text("Vendor vs market");
                Ui.Muted2("Re-routes rows: show-only when the market clearly beats the vendor, vendor sell when a discard has vendor value. Needs Universalis.");
                ImGui.TableNextColumn();
                Ui.Muted2("—");
            }
        }
        if (editingCharacter)
        {
            ImGui.Spacing();
            OverrideToggle(nameof(Profile.RuleActionOverrides));
            Ui.Text("Override preferred actions for this character");
        }
    }

    private void DrawActionOverride(Profile p, string ruleId, bool enabled)
    {
        var options = new List<string> { "rule default", ActionKind.Discard.Label(), ActionKind.VendorSell.Label(), ActionKind.ExpertDelivery.Label(), ActionKind.Desynth.Label() };
        var kinds = new[] { ActionKind.None, ActionKind.Discard, ActionKind.VendorSell, ActionKind.ExpertDelivery, ActionKind.Desynth };
        var idx = p.RuleActionOverrides.TryGetValue(ruleId, out var a) ? Array.IndexOf(kinds, a) : 0;
        if (idx < 0) idx = 0;
        ImGui.SetNextItemWidth(-1);
        using (ImRaii.Disabled(!enabled))
        {
            if (Ui.Combo($"##ao{ruleId}", ref idx, options))
            {
                if (idx == 0) p.RuleActionOverrides.Remove(ruleId); else p.RuleActionOverrides[ruleId] = kinds[idx];
                dirty = true;
            }
        }
        Ui.Tooltip("Used when it is one of the alternatives the rule allows for that item; otherwise the rule's own choice stands.");
    }

    // ---------- thresholds ----------

    private void DrawThresholds()
    {
        var p = Editing;
        var enabled = OverrideToggle(nameof(Profile.Thresholds));
        Ui.Text($"Current preset: {Presets.Detect(p.Thresholds)}");
        Ui.Muted2("Every number the rules and caps read. Hover a label for what it does.");
        using var dis = ImRaii.Disabled(!enabled);
        var t = p.Thresholds;
        var c = false;

        Ui.Header("Gear");
        c |= Int("Obsolete gear level gap", ref t, x => x.ObsoleteGearLevelGap, (x, v) => x.ObsoleteGearLevelGap = v, "Gear is obsolete when the best job that can wear it is at least this many levels above the gear's equip level.");
        var unplayed = t.IncludeGearForUnplayedJobs;
        if (ImGui.Checkbox("Include gear for jobs never levelled", ref unplayed)) { t.IncludeGearForUnplayedJobs = unplayed; c = true; }
        Ui.HelpMarker("Proposes gear whose entire job category sits at level 1. Medium confidence.");

        Ui.Header("Consumables and materials");
        c |= Int("Consumable item level gap", ref t, x => x.ConsumableItemLevelGap, (x, v) => x.ConsumableItemLevelGap = v, "Food and medicine are outleveled when their item level is this far below your best gearset item level.");
        c |= Int("Crafting mat: max recipe level", ref t, x => x.CraftingMatMaxRecipeLevel, (x, v) => x.CraftingMatMaxRecipeLevel = v, "A material counts as unusable only if every recipe using it is at or below this level…");
        c |= Int("Crafting mat: crafter lead", ref t, x => x.CraftingMatCrafterLeadLevels, (x, v) => x.CraftingMatCrafterLeadLevels = v, "…and the relevant crafter is at least this many levels past each of those recipes.");

        Ui.Header("Vendor and market");
        var vp = t.VendorOnlyMaxUnitPrice;
        ImGui.SetNextItemWidth(140 * Ui.Scale);
        if (Ui.InputUInt("Vendor-only junk: max unit price", ref vp)) { t.VendorOnlyMaxUnitPrice = vp; c = true; }
        Ui.HelpMarker("Vendor-only items worth more than this per unit are left alone.");
        var factor = t.MarketPremiumFactor;
        ImGui.SetNextItemWidth(200 * Ui.Scale);
        if (Ui.SliderDouble("Market premium factor", ref factor, 1.0, 5.0, "%.1fx")) { t.MarketPremiumFactor = factor; c = true; }
        Ui.HelpMarker("Market beats vendor when market × (1 − tax) exceeds vendor × this.");
        var minMk = t.MarketMinStackValueGil;
        ImGui.SetNextItemWidth(140 * Ui.Scale);
        if (Ui.InputLong("Market: min stack value", ref minMk)) { t.MarketMinStackValueGil = minMk; c = true; }
        Ui.HelpMarker("Only flag market value when the whole stack is worth at least this much.");
        var tax = t.MarketTaxRate;
        ImGui.SetNextItemWidth(200 * Ui.Scale);
        if (Ui.SliderDouble("Market tax rate", ref tax, 0.0, 0.1, "%.2f")) { t.MarketTaxRate = tax; c = true; }

        Ui.Header("Run caps");
        c |= Int("Soft cap: items per run", ref t, x => x.SoftCapItems, (x, v) => x.SoftCapItems = v, "Beyond this, Accept needs a second click.");
        var capGil = t.SoftCapGil;
        ImGui.SetNextItemWidth(140 * Ui.Scale);
        if (Ui.InputLong("Soft cap: gil at risk per run", ref capGil, 5000)) { t.SoftCapGil = capGil; c = true; }
        Ui.HelpMarker("Vendor value of everything destroyed or sold in one run. Whichever cap trips first counts.");

        if (c) { p.Preset = Presets.Detect(t); dirty = true; }
    }

    private bool Int(string label, ref Thresholds t, Func<Thresholds, int> get, Action<Thresholds, int> set, string help)
    {
        var v = get(t);
        ImGui.SetNextItemWidth(140 * Ui.Scale);
        var changed = Ui.InputInt(label, ref v);
        if (changed) set(t, Math.Max(0, v));
        Ui.HelpMarker(help);
        return changed;
    }

    // ---------- lists ----------

    private void DrawLists()
    {
        Ui.Header("Hard blocks (not editable)");
        Ui.Muted2("Always excluded: items the game forbids discarding, unique untradeables, anything in a gearset or glamour plate, currencies, crystals, minions, mounts, orchestrion rolls, housing items. The always-discard list cannot override these.");
        protectEditor.Draw();
        alwaysEditor.Draw();
    }

    // ---------- containers ----------

    private void DrawContainers()
    {
        var p = Editing;
        Ui.Header("Scan these containers");
        var en = OverrideToggle(nameof(Profile.ContainerEnabled));
        using (ImRaii.Disabled(!en))
        {
            foreach (var kind in Enum.GetValues<ContainerKind>())
            {
                var on = p.IsContainerEnabled(kind);
                if (ImGui.Checkbox($"{kind.DisplayName()}##en{kind}", ref on)) { p.ContainerEnabled[kind] = on; dirty = true; }
            }
        }

        Ui.Header("Open the window automatically when a container opens");
        var ao = OverrideToggle(nameof(Profile.AutoOpenOnContainer));
        using (ImRaii.Disabled(!ao))
        {
            foreach (var kind in new[] { ContainerKind.Saddlebag, ContainerKind.Retainer, ContainerKind.GlamourDresser })
            {
                var on = p.IsAutoOpen(kind);
                if (ImGui.Checkbox($"{kind.DisplayName()}##ao{kind}", ref on)) { p.AutoOpenOnContainer[kind] = on; dirty = true; }
            }
            Ui.Muted2("Only opens when there is something cleanable in that container. Items you accepted earlier for a closed container re-open it regardless.");
        }

        Ui.Header("Retainers");
        var ex = OverrideToggle(nameof(Profile.ExcludedRetainerIds));
        using (ImRaii.Disabled(!ex))
        {
            var known = coordinator.RetainerNames;
            if (known.Count == 0) Ui.Muted2("Summon a retainer once so Tidy Up learns their names.");
            foreach (var (id, name) in known)
            {
                var excluded = p.ExcludedRetainerIds.Contains(id);
                if (ImGui.Checkbox($"Never scan {name}##ret{id}", ref excluded)) { if (excluded) p.ExcludedRetainerIds.Add(id); else p.ExcludedRetainerIds.Remove(id); dirty = true; }
            }
        }
        var col = OverrideToggle(nameof(Profile.RetainerSectionsCollapsed));
        using (ImRaii.Disabled(!col))
        {
            var collapsed = p.RetainerSectionsCollapsed;
            if (ImGui.Checkbox("Retainer sections start collapsed", ref collapsed)) { p.RetainerSectionsCollapsed = collapsed; dirty = true; }
            Ui.HelpMarker("Venture rewards and market returns land in retainers; a collapsed section forces a deliberate look.");
        }
    }

    // ---------- notifications ----------

    private void DrawNotifications()
    {
        var p = Editing;
        Ui.Header("Server info bar");
        var dtr = OverrideToggle(nameof(Profile.ShowDtrEntry));
        using (ImRaii.Disabled(!dtr))
        {
            var on = p.ShowDtrEntry;
            if (ImGui.Checkbox("Show \"used/total · N cleanable\" in the server info bar", ref on)) { p.ShowDtrEntry = on; dirty = true; }
        }
        var np = OverrideToggle(nameof(Profile.FullnessNudgePercent));
        using (ImRaii.Disabled(!np))
        {
            var pct = p.FullnessNudgePercent;
            ImGui.SetNextItemWidth(200 * Ui.Scale);
            if (ImGui.SliderInt("Toast when inventory is this full", ref pct, 50, 100, "%d%%", ImGuiSliderFlags.None)) { p.FullnessNudgePercent = pct; dirty = true; }
        }

        Ui.Header("After duties");
        var pd = OverrideToggle(nameof(Profile.PostDutyNudge));
        using (ImRaii.Disabled(!pd))
        {
            var on = p.PostDutyNudge;
            if (ImGui.Checkbox("Toast \"N items cleanable\" a few seconds after a duty ends", ref on)) { p.PostDutyNudge = on; dirty = true; }
        }

        Ui.Header("Runs");
        var sm = OverrideToggle(nameof(Profile.StackMergeBeforeScan));
        using (ImRaii.Disabled(!sm))
        {
            var on = p.StackMergeBeforeScan;
            if (ImGui.Checkbox("Merge split stacks before every scan (non-destructive)", ref on)) { p.StackMergeBeforeScan = on; dirty = true; }
            Ui.HelpMarker("Runs silently; a chat line reports how many stacks merged.");
        }
        var chatSummary = config.ChatSummaryAfterRun;
        if (ImGui.Checkbox("Chat summary after each run", ref chatSummary)) { config.ChatSummaryAfterRun = chatSummary; dirty = true; }
        var pad = config.GamepadNavigation;
        if (ImGui.Checkbox("Gamepad navigation in the confirmation window (D-pad move, A toggle, X accept, B cancel)", ref pad)) { config.GamepadNavigation = pad; dirty = true; }
    }

    // ---------- integrations ----------

    private void DrawIntegrations()
    {
        Ui.Header("Universalis");
        var uni = config.UseUniversalis;
        if (ImGui.Checkbox("Fetch market prices from Universalis", ref uni)) { config.UseUniversalis = uni; dirty = true; }
        Ui.Muted2("Prices are cached 15 minutes and fetched 100 items at a time. A failed fetch just means \"market unknown\".");

        Ui.Header("Allagan Tools");
        var at = config.UseAllaganTools;
        if (ImGui.Checkbox("Use Allagan Tools for closed containers and alt characters", ref at)) { config.UseAllaganTools = at; allagan.Enabled = at; dirty = true; }
        Ui.Muted2(allagan.IsInstalled ? (allagan.IsAvailable ? "Allagan Tools: connected." : "Allagan Tools: installed, waiting for it to initialise.") : "Allagan Tools is not installed. Closed containers only appear in the window while they are open.");
        var alts = config.ShowAltSections;
        if (ImGui.Checkbox("Show read-only sections for alt characters", ref alts)) { config.ShowAltSections = alts; dirty = true; }

        Ui.Header("Import from Discard Helper");
        Ui.Muted2("Reads an ARDiscard configuration file and adds its item ids to the always-discard list.");
        ImGui.SetNextItemWidth(-160 * Ui.Scale);
        Ui.InputText("##import", @"C:\Users\you\AppData\Roaming\XIVLauncher\pluginConfigs\ARDiscard.json", ref importPath, 512);
        ImGui.SameLine();
        if (Ui.Button("Import", 140 * Ui.Scale))
        {
            try
            {
                var ids = Core.Integrations.DiscardHelperImport.ParseItemIds(File.ReadAllText(importPath));
                var added = ids.Count(id => config.AlwaysDiscardList.Add(id, note: "Imported from Discard Helper"));
                importResult = $"Imported {added} new item ids ({ids.Count} found).";
                dirty = true;
            }
            catch (Exception ex)
            {
                importResult = $"Import failed: {ex.Message}";
            }
        }
        if (!string.IsNullOrEmpty(importResult)) Ui.Muted2(importResult);
    }

    // ---------- advanced ----------

    private void DrawAdvanced()
    {
        Ui.Header("Timing");
        var cb = config.Callbacks;
        var rl = cb.RateLimitMs;
        ImGui.SetNextItemWidth(140 * Ui.Scale);
        if (Ui.InputInt("Pause between actions (ms)", ref rl, 50)) { cb.RateLimitMs = Math.Clamp(rl, 100, 5000); dirty = true; }
        var to = cb.ActionTimeoutMs;
        ImGui.SetNextItemWidth(140 * Ui.Scale);
        if (Ui.InputInt("Per-action timeout (ms)", ref to, 500)) { cb.ActionTimeoutMs = Math.Clamp(to, 1000, 30000); dirty = true; }

        Ui.Header("Native dialog answers");
        Ui.Muted2("Which button Tidy Up presses on each game dialog. Change only if a spike shows the default is wrong.");
        if (Ui.Button("Open the spike window")) openDebug();
        Ui.Muted2(config.SpikesVerified ? "Spikes marked verified on this machine." : "Spikes not yet verified: run Phase 0 in the spike window before trusting a full run.");

        Ui.Header("Per-character overrides on file");
        foreach (var o in config.Profiles.Overrides.ToList())
        {
            Ui.Text($"{o.CharacterName} ({o.CharacterId:X}) · {o.OverriddenProperties.Count} overridden");
            ImGui.SameLine();
            if (Ui.Button($"Remove##{o.CharacterId}")) { config.Profiles.Overrides.Remove(o); dirty = true; }
        }
        if (config.Profiles.Overrides.Count == 0) Ui.Muted2("None.");
    }
}
