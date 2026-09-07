using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using TidyUp.Core.Lists;
using TidyUp.Game;
using TidyUp.Integrations;
using TidyUp.Services;

namespace TidyUp.Windows;

/// <summary>
/// Settings as a sidebar of pages. Each page is a short list of label + control rows; help lives in
/// tooltips, not in paragraphs. Edits the account profile unless the character switch at the top is on.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private enum Page { General, Rules, Lists, Containers, Automation, Notifications, Integrations, Advanced }

    /// <summary>Set by the plugin so the Automation page can show dependency status.</summary>
    public Automation.AutoPilot? Pilot { get; set; }
    public Integrations.VnavmeshIpc? Nav { get; set; }
    public Integrations.LifestreamIpc? Travel { get; set; }

    private readonly Configuration config;
    private readonly IPlayerState player;
    private readonly ItemDatabase db;
    private readonly IconCache icons;
    private readonly AllaganToolsSource allagan;
    private readonly RunCoordinator coordinator;
    private readonly Action openDebug;
    private readonly ListEditor protectEditor;
    private readonly ListEditor alwaysEditor;

    private Page page = Page.General;
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
        protectEditor = new ListEditor(db, icons, () => config.ProtectList, "Protect list", "Never proposed, whatever the rules say.", () => player.ContentId, MarkDirty);
        alwaysEditor = new ListEditor(db, icons, () => config.AlwaysDiscardList, "Always discard", "Proposed on every run. Protected items and hard rules still win.", () => player.ContentId, MarkDirty);
        Size = new Vector2(760, 540);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(600, 380), MaximumSize = new Vector2(4000, 3000) };
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
        var navWidth = 150 * Ui.Scale;
        using (var nav = ImRaii.Child("##nav", new Vector2(navWidth, 0), false, ImGuiWindowFlags.None))
        {
            if (nav) DrawNav();
        }
        ImGui.SameLine();
        using (var body = ImRaii.Child("##body", new Vector2(0, 0), false, ImGuiWindowFlags.None))
        {
            if (body)
            {
                DrawScopeSwitch();
                switch (page)
                {
                    case Page.General: DrawGeneral(); break;
                    case Page.Rules: DrawRules(); break;
                    case Page.Lists: DrawLists(); break;
                    case Page.Containers: DrawContainers(); break;
                    case Page.Automation: DrawAutomation(); break;
                    case Page.Notifications: DrawNotifications(); break;
                    case Page.Integrations: DrawIntegrations(); break;
                    case Page.Advanced: DrawAdvanced(); break;
                }
            }
        }

        if (dirty)
        {
            dirty = false;
            config.Save(PluginServices.PluginInterface);
        }
    }

    private void DrawNav()
    {
        Ui.Gap(0.5f);
        foreach (var p in Enum.GetValues<Page>())
        {
            var selected = p == page;
            using var c = ImRaii.PushColor(ImGuiCol.Header, Ui.Accent * new Vector4(1, 1, 1, 0.25f), selected);
            using var t = ImRaii.PushColor(ImGuiCol.Text, Ui.Muted, !selected);
            if (ImGui.Selectable($"  {p}", selected, ImGuiSelectableFlags.None, new Vector2(0, 26 * Ui.Scale))) page = p;
        }
    }

    private void DrawScopeSwitch()
    {
        var name = player.IsLoaded ? player.CharacterName : "not logged in";
        var scope = editingCharacter ? 1 : 0;
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
