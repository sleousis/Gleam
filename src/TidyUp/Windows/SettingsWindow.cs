using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using TidyUp.Core.Lists;
using TidyUp.Core.Model;
using TidyUp.Core.Rules;
using TidyUp.Game;
using TidyUp.Integrations;
using TidyUp.Services;

namespace TidyUp.Windows;

/// <summary>
/// One page. What matters is at the top: the preset and what it means, which containers, hands-free
/// on or off, and the two lists. Everything else is a knob most people never touch and lives behind
/// one "Advanced" fold at the bottom.
/// </summary>
public sealed partial class SettingsWindow
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

    private bool dirty;
    private string importPath = string.Empty;
    private string importResult = string.Empty;

    /// <summary>Set by the plugin so the Automation section can show dependency status.</summary>
    public Automation.AutoPilot? Pilot { get; set; }
    public VnavmeshIpc? Nav { get; set; }
    public LifestreamIpc? Travel { get; set; }
    public AutoRetainerIpc? AutoRetainer { get; set; }

    private static readonly IReadOnlyList<(PresetName, string)> PresetOptions =
    [
        (PresetName.MarketBoard, PresetName.MarketBoard.Label()), (PresetName.Vendor, PresetName.Vendor.Label()), (PresetName.DiscardAll, PresetName.DiscardAll.Label()),
    ];

    /// <summary>Set by the host window: the way back to the list.</summary>
    public Action? Back { get; set; }

    public SettingsWindow(Configuration config, IPlayerState player, ItemDatabase db, IconCache icons, AllaganToolsSource allagan, RunCoordinator coordinator, Action openDebug)
    {
        this.config = config;
        this.player = player;
        this.db = db;
        this.icons = icons;
        this.allagan = allagan;
        this.coordinator = coordinator;
        this.openDebug = openDebug;
        protectEditor = new ListEditor(db, icons, () => config.ProtectList, "Keep these", "Gleam never lists these, whatever else you choose.", () => player.ContentId, MarkDirty);
        alwaysEditor = new ListEditor(db, icons, () => config.AlwaysDiscardList, "Always junk", "Gleam lists these every time, even when no rule picks them.", () => player.ContentId, MarkDirty);
    }

    private void MarkDirty() => dirty = true;

    /// <summary>The profile the controls edit.</summary>
    private Profile Editing => config.Profiles.Account;

    public void Draw()
    {
        var version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "dev";
        Ui.Header(icons.LogoSmall, "Settings", $"Gleam v{version} · by Raiden Shinryu");
        if (Back is not null) { if (Ui.BackLink()) Back(); Ui.Gap(0.2f); }
        using (var body = ImRaii.Child("##body", new Vector2(0, 0), false, ImGuiWindowFlags.None))
        {
            if (body)
            {
                Ui.Gap(0.2f);
                DrawEssentials();
                Ui.Gap(0.5f);
                if (config.AdvancedMode && ImGui.CollapsingHeader("More", ImGuiTreeNodeFlags.None)) DrawAdvancedFold();
            }
        }

        if (dirty)
        {
            dirty = false;
            config.Save(PluginServices.PluginInterface);
        }
    }

    // ---------- the page most people see ----------

    private void DrawEssentials()
    {
        var p = Editing;

        using (Ui.Card("purpose"))
        {
            Ui.TextColored(Ui.Muted, "WHAT GLEAM DOES FOR YOU");
            ImGui.Spacing();
            var clean = config.UseClean;
            if (Ui.Check("Clear out my junk", ref clean)) { config.UseClean = clean || !config.UseOrganize; dirty = true; }
            Ui.Tooltip("Off: Gleam never suggests throwing anything away or selling it.");
            ImGui.SameLine(0, 24 * Ui.Scale);
            var org = config.UseOrganize;
            if (Ui.Check("Put my things away", ref org)) { config.UseOrganize = org || !config.UseClean; dirty = true; }
            Ui.Tooltip("Off: Gleam never moves anything between your bags, saddlebag and retainers.");
        }

        if (config.UseClean)
        using (Ui.Card("junk"))
        {
            Ui.TextColored(Ui.Muted, "WHAT TO DO WITH JUNK");
            ImGui.Spacing();
            var preset = Presets.Detect(p.Thresholds);
            if (Ui.Segmented("##preset", ref preset, PresetOptions)) { p.ApplyPreset(preset); dirty = true; }
            Ui.TextColored(Ui.Accent, p.Thresholds.Policy.Describe());
            Ui.Hint("You always see the full list and can change any row before anything happens.");
            if (p.Thresholds.Policy == ActionPolicy.MarketListTradeable)
            {
                Ui.Gap(0.3f);
                ImGui.AlignTextToFramePadding();
                Ui.Hint("List in stacks of");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(70 * Ui.Scale);
                var per = config.MarketListStackSize;
                if (ImGui.InputInt("##mstack", ref per, 0, 0, "%d", ImGuiInputTextFlags.None)) { config.MarketListStackSize = Math.Clamp(per, 0, 9999); dirty = true; }
                Ui.Tooltip("Smaller listings sell faster. 0 lists the whole stack at once.");
                ImGui.SameLine();
                Ui.Hint(per == 0 ? "(whole stacks)" : "per listing");
            }
            Ui.Gap(0.3f);
            var sortAfter = config.SortAfterRun;
            if (Ui.Check(ConfirmationWindow.SortAfterLabel, ref sortAfter)) { config.SortAfterRun = sortAfter; dirty = true; }
            Ui.Tooltip(ConfirmationWindow.SortAfterHint);
        }

        if (config.AdvancedMode)
        using (Ui.Card("where"))
        {
            Ui.TextColored(Ui.Muted, "WHERE TO LOOK");
            ImGui.Spacing();
            var kinds = Enum.GetValues<ContainerKind>();
            for (var i = 0; i < kinds.Length; i++)
            {
                var kind = kinds[i];
                var on = p.IsContainerEnabled(kind);
                if (Ui.Check($"{kind.DisplayName()}##en{kind}", ref on)) { p.ContainerEnabled[kind] = on; dirty = true; }
                if (i < kinds.Length - 1 && i != 2) ImGui.SameLine();
            }
        }

        DrawRequiredPlugins();

        if (config.AdvancedMode)
        using (Ui.Card("ventures"))
        {
            Ui.TextColored(Ui.Muted, "AFTER VENTURES");
            ImGui.Spacing();
            var a = config.Automation;
            var after = a.CleanAfterVentures;
            if (Ui.Check("Discard junk from my bags when AutoRetainer finishes a retainer", ref after)) { a.CleanAfterVentures = after; dirty = true; }
            ImGui.SameLine();
            if (AutoRetainer is null || !AutoRetainer.IsInstalled) Ui.Pill("needs the AutoRetainer plugin", Ui.Warn); else Ui.Pill("AutoRetainer", Ui.Ok, Dalamud.Interface.FontAwesomeIcon.Check);
            Ui.HintWrapped("Only rows the rules would tick on their own, and only discards. Selling and listing still wait for your review.");
        }

        using (Ui.Card("protect")) protectEditor.Draw();
        if (config.AdvancedMode) using (Ui.Card("always")) alwaysEditor.Draw();

        using (Ui.Card("mode"))
        {
            Ui.TextColored(Ui.Muted, "SHOW MORE");
            ImGui.Spacing();
            var adv = config.AdvancedMode;
            if (Ui.Check("Show advanced options", ref adv)) { config.AdvancedMode = adv; dirty = true; }
            Ui.HintWrapped("Filters and sorting, a choice of action on every row, layouts and rules, and every other setting. Off keeps Gleam to one list and one button.");
        }
    }

    /// <summary>
    /// Gleam walks and travels by itself, so the two plugins that make that possible are requirements, not
    /// options. This says so plainly and shows at a glance whether they are there.
    /// </summary>
    private void DrawRequiredPlugins()
    {
        var haveNav = Nav is { IsInstalled: true };
        var haveTravel = Travel is { IsInstalled: true };

        using (Ui.Card("auto"))
        {
            Ui.TextColored(Ui.Muted, "PLUGINS GLEAM NEEDS");
            ImGui.Spacing();
            Ui.HintWrapped("Gleam does the walking itself. It opens the saddlebag, travels to an inn for your retainers and the dresser, and visits your Grand Company when something is to be turned in. Both of these free plugins have to be installed for that.");
            Ui.Gap(0.4f);

            Requirement("vnavmesh", haveNav, "Walks your character from place to place, to the bell, the dresser and the merchant.");
            Requirement("Lifestream", haveTravel, "Teleports between aetherytes and into inns, to reach a retainer bell.");

            if (!haveNav || !haveTravel)
            {
                Ui.Gap(0.4f);
                var which = !haveNav && !haveTravel ? "vnavmesh and Lifestream" : !haveNav ? "vnavmesh" : "Lifestream";
                Ui.TextColored(Ui.Danger, $"Install {which} to let Gleam finish a run.");
                Ui.HintWrapped("Until then Gleam still works on whatever you have open yourself, and anything further away waits.");
            }

            if (config.AdvancedMode)
            {
                Ui.Gap(0.4f);
                var a = config.Automation;
                var cleanUnseen = a.UnseenRows != UnseenRowsMode.Skip;
                if (Ui.Check("Also clean items found along the way", ref cleanUnseen)) { a.UnseenRows = cleanUnseen ? UnseenRowsMode.Clean : UnseenRowsMode.Skip; dirty = true; }
                Ui.Tooltip("Retainers and the dresser only show their contents once open. On: those items are cleaned by the same rules on the spot. Off: they wait for your next review.");
            }
        }
    }

    /// <summary>One required plugin: its state first, then its name, then what Gleam uses it for.</summary>
    private static void Requirement(string name, bool installed, string what)
    {
        if (installed) Ui.Pill("installed", Ui.Ok, Dalamud.Interface.FontAwesomeIcon.Check);
        else Ui.Pill("missing", Ui.Danger, Dalamud.Interface.FontAwesomeIcon.ExclamationTriangle);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        Ui.Text(name);
        ImGui.SameLine();
        Ui.Hint(what);
    }

    // ---------- everything else, folded ----------

    private void DrawAdvancedFold()
    {
        Ui.Gap(0.5f);
        Fold("What counts as junk", DrawRules);
        Fold("Retainers", DrawContainers);
        Fold("Notifications", DrawNotifications);
        Fold("Other characters", DrawIntegrations);

        Ui.Gap(0.5f);
        if (Ui.LinkButton("Troubleshooting")) openDebug();
        Ui.Tooltip("Only needed when a step keeps failing after a game update.");
    }

    private static void Fold(string title, Action body)
    {
        using var id = ImRaii.PushId(title);
        if (!ImGui.CollapsingHeader(title, ImGuiTreeNodeFlags.None)) return;
        using var indent = ImRaii.PushIndent(12f, true, true);
        Ui.Gap(0.3f);
        body();
        Ui.Gap(0.5f);
    }
}
