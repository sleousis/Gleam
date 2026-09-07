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
public sealed partial class SettingsWindow : StyledWindow
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

    private static readonly IReadOnlyList<(PresetName, string)> PresetOptions =
    [
        (PresetName.MarketBoard, PresetName.MarketBoard.Label()), (PresetName.Vendor, PresetName.Vendor.Label()), (PresetName.DiscardAll, PresetName.DiscardAll.Label()),
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

    /// <summary>The profile the controls edit.</summary>
    private Profile Editing => config.Profiles.Account;

    public override void Draw()
    {
        using (var body = ImRaii.Child("##body", new Vector2(0, 0), false, ImGuiWindowFlags.None))
        {
            if (body)
            {
                var version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "dev";
                Ui.Header(icons.Logo, "Tidy Up", $"v{version} · by Raiden Shinryu");
                Ui.Gap(0.6f);
                DrawEssentials();
                Ui.Gap(0.5f);
                if (ImGui.CollapsingHeader("More", ImGuiTreeNodeFlags.None)) DrawAdvancedFold();
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

        using (Ui.Card("junk"))
        {
            Ui.TextColored(Ui.Muted, "WHAT TO DO WITH JUNK");
            ImGui.Spacing();
            var preset = Presets.Detect(p.Thresholds);
            if (Ui.Segmented("##preset", ref preset, PresetOptions)) { p.ApplyPreset(preset); dirty = true; }
            Ui.TextColored(Ui.Accent, p.Thresholds.Policy.Describe());
            Ui.Hint("You always see the full list and can change any row before anything happens.");
        }

        using (Ui.Card("where"))
        {
            Ui.TextColored(Ui.Muted, "WHERE TO LOOK");
            ImGui.Spacing();
            var kinds = Enum.GetValues<ContainerKind>();
            for (var i = 0; i < kinds.Length; i++)
            {
                var kind = kinds[i];
                var on = p.IsContainerEnabled(kind);
                if (ImGui.Checkbox($"{kind.DisplayName()}##en{kind}", ref on)) { p.ContainerEnabled[kind] = on; dirty = true; }
                if (i < kinds.Length - 1 && i != 2) ImGui.SameLine();
            }
        }

        using (Ui.Card("auto"))
        {
            Ui.TextColored(Ui.Muted, "HANDS-FREE");
            ImGui.Spacing();
            var a = config.Automation;
            var auto = a.Enabled;
            if (ImGui.Checkbox("Do the travelling for me", ref auto)) { a.Enabled = auto; dirty = true; }
            ImGui.SameLine();
            if (Nav is null || !Nav.IsInstalled) Ui.Pill("needs the vnavmesh plugin", Ui.Warn); else Ui.Pill("vnavmesh", Ui.Ok, Dalamud.Interface.FontAwesomeIcon.Check);
            ImGui.SameLine();
            if (Travel is null || !Travel.IsInstalled) Ui.Pill("needs the Lifestream plugin", Ui.Warn); else Ui.Pill("Lifestream", Ui.Ok, Dalamud.Interface.FontAwesomeIcon.Check);
            Ui.HintWrapped("Opens the saddlebag, teleports to an inn for the retainers and the dresser, then visits a merchant and your Grand Company as needed. Gameplay automation; use at your own risk.");
            using (ImRaii.Disabled(!auto))
            {
                var cleanUnseen = a.UnseenRows != UnseenRowsMode.Skip;
                if (ImGui.Checkbox("Also clean items discovered along the way", ref cleanUnseen)) { a.UnseenRows = cleanUnseen ? UnseenRowsMode.Clean : UnseenRowsMode.Skip; dirty = true; }
                Ui.Tooltip("Retainers and the dresser only show their contents once open. On: they are cleaned by the same rules on the spot. Off: they wait for your next review.");
            }
        }

        using (Ui.Card("protect")) protectEditor.Draw();
        using (Ui.Card("always")) alwaysEditor.Draw();
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
        Ui.Tooltip("Only needed if a step keeps failing after a game update.");
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
