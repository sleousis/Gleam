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

/// <summary>
/// One page. What matters is at the top: the preset and what it means, which containers, hands-free
/// on or off, and the two lists. Everything else is a knob most people never touch and lives behind
/// one "Advanced" fold at the bottom.
/// </summary>
public sealed partial class SettingsWindow : Window
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

    /// <summary>Set by the plugin so the Automation section can show dependency status.</summary>
    public Automation.AutoPilot? Pilot { get; set; }
    public VnavmeshIpc? Nav { get; set; }
    public LifestreamIpc? Travel { get; set; }

    private static readonly IReadOnlyList<(PresetName, string)> PresetOptions =
    [
        (PresetName.Cautious, "Cautious"), (PresetName.Balanced, "Balanced"), (PresetName.Aggressive, "Aggressive"),
    ];

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
        protectEditor = new ListEditor(db, icons, () => config.ProtectList, "Never touch", "Items here are never proposed, whatever the preset says.", () => player.ContentId, MarkDirty);
        alwaysEditor = new ListEditor(db, icons, () => config.AlwaysDiscardList, "Always clean", "Items here are proposed on every run.", () => player.ContentId, MarkDirty);
        Size = new Vector2(620, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(480, 360), MaximumSize = new Vector2(4000, 3000) };
    }

    private void MarkDirty() => dirty = true;

    /// <summary>The profile the controls edit right now.</summary>
    private Profile Editing => editingCharacter
        ? config.Profiles.GetOrCreateOverride(player.ContentId, player.CharacterName).Values
        : config.Profiles.Account;

    /// <summary>In character mode, draws the override toggle for a setting and returns whether its control is live.</summary>
    private bool Override(string prop)
    {
        if (!editingCharacter) return true;
        var on = config.Profiles.IsOverridden(player.ContentId, prop);
        if (ImGui.Checkbox($"##ov{prop}", ref on))
        {
            config.Profiles.SetOverridden(player.ContentId, player.CharacterName, prop, on);
            dirty = true;
        }
        Ui.Tooltip(on ? "Overridden for this character. Untick to follow the account." : "Following the account. Tick to override for this character.");
        ImGui.SameLine();
        return on;
    }

    public override void Draw()
    {
        using (var body = ImRaii.Child("##body", new Vector2(0, 0), false, ImGuiWindowFlags.None))
        {
            if (body)
            {
                DrawEssentials();
                Ui.Gap(1.5f);
                ImGui.Separator();
                if (ImGui.CollapsingHeader("Advanced", ImGuiTreeNodeFlags.None)) DrawAdvancedFold();
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

        Ui.Section("What to do with junk");
        var live = Override(nameof(Profile.Thresholds));
        using (ImRaii.Disabled(!live))
        {
            var preset = Presets.Detect(p.Thresholds);
            if (Ui.Segmented("##preset", ref preset, PresetOptions)) { p.ApplyPreset(preset); dirty = true; }
            if (preset == PresetName.Custom) { ImGui.SameLine(); Ui.Hint("custom"); }
        }
        Ui.TextColored(Ui.Accent, p.Thresholds.Policy.Describe());
        Ui.Hint("You always see the full list and can change any row before anything happens.");

        Ui.Section("Where to look");
        var en = Override(nameof(Profile.ContainerEnabled));
        using (ImRaii.Disabled(!en))
        {
            var kinds = Enum.GetValues<ContainerKind>();
            for (var i = 0; i < kinds.Length; i++)
            {
                var kind = kinds[i];
                var on = p.IsContainerEnabled(kind);
                if (ImGui.Checkbox($"{kind.DisplayName()}##en{kind}", ref on)) { p.ContainerEnabled[kind] = on; dirty = true; }
                if (i < kinds.Length - 1 && i != 2) ImGui.SameLine();
            }
        }

        Ui.Section("Hands-free");
        var a = config.Automation;
        var auto = a.Enabled;
        if (ImGui.Checkbox("Do the walking for me", ref auto)) { a.Enabled = auto; dirty = true; }
        ImGui.SameLine();
        if (Nav is null || !Nav.IsInstalled) Ui.Pill("needs vnavmesh", Ui.Warn); else Ui.Pill("vnavmesh", Ui.Ok);
        ImGui.SameLine();
        if (Travel is null || !Travel.IsInstalled) Ui.Pill("needs Lifestream", Ui.Warn); else Ui.Pill("Lifestream", Ui.Ok);
        Ui.Hint("Opens the saddlebag, travels to an inn, visits every retainer and the dresser, then your Grand Company and a merchant. This is gameplay automation and is against the game's terms.");

        protectEditor.Draw();
        alwaysEditor.Draw();
    }

    // ---------- everything else, folded ----------

    private void DrawAdvancedFold()
    {
        Ui.Gap(0.5f);
        DrawScopeSwitch();

        Fold("Rules and thresholds", DrawRules);
        Fold("Notifications", DrawNotifications);
        Fold("Hands-free details", DrawAutomation);
        Fold("Retainers and auto-open", DrawContainers);
        Fold("Integrations", DrawIntegrations);
        Fold("Timing and verification", DrawAdvanced);
    }

    private static void Fold(string title, Action body)
    {
        using var id = ImRaii.PushId(title);
        if (!ImGui.CollapsingHeader(title, ImGuiTreeNodeFlags.None)) return;
        using var indent = ImRaii.PushIndent(12f, true, true);
        body();
        Ui.Gap(0.5f);
    }

    private void DrawScopeSwitch()
    {
        var name = player.IsLoaded ? player.CharacterName : "not logged in";
        var scope = editingCharacter ? 1 : 0;
        Ui.Hint("Editing");
        ImGui.SameLine();
        using (ImRaii.Disabled(!player.IsLoaded))
        {
            if (Ui.Segmented("##scope", ref scope, [(0, "Account"), (1, name)])) editingCharacter = scope == 1;
        }
        Ui.Tooltip(editingCharacter
            ? "Editing this character only. Tick the box beside a setting to override it; unticked settings follow the account."
            : "Editing the defaults every character uses.");
        Ui.Gap(0.5f);
    }
}
