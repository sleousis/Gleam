using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Gleam.Core.Lists;
using Gleam.Core.Model;
using Gleam.Core.Rules;
using Gleam.Game;
using Gleam.Integrations;
using Gleam.Services;

namespace Gleam.Windows;

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
    private string importResultText = string.Empty;
    private DateTime importResultUntil;

    /// <summary>What the Discard Helper row last said. Setting it restarts the few seconds it stays on screen.</summary>
    private string ImportResult
    {
        get => importResultText;
        set { importResultText = value; importResultUntil = DateTime.UtcNow.AddSeconds(6); }
    }
    private string? importFound;
    private Core.Integrations.DiscardHelperLists? importLists;

    /// <summary>Dalamud's own file browser, drawn by this window so it can sit above the game.</summary>
    private readonly Dalamud.Interface.ImGuiFileDialog.FileDialogManager FileDialogs = new();

    /// <summary>Set by the plugin so the Automation section can show dependency status.</summary>
    public Automation.AutoPilot? Pilot { get; set; }

    /// <summary>Set by the plugin: puts the bug report on the clipboard.</summary>
    public Action? CopyReport { get; set; }
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
        protectEditor = new ListEditor(db, icons, () => config.ProtectList, "Anything Gleam must never touch?", "Add an item and Gleam leaves it alone, whatever the rules say.", () => player.ContentId, MarkDirty);
        alwaysEditor = new ListEditor(db, icons, () => config.AlwaysDiscardList, "Anything that is always junk?", "Add an item and Gleam lists it every time, even when no rule picks it.", () => player.ContentId, MarkDirty);
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
                if (config.AdvancedMode)
                {
                    Ui.Gap(0.3f);
                    if (ImGui.CollapsingHeader("Fine detail", ImGuiTreeNodeFlags.None))
                    {
                        using var fade = Ui.FoldFade("fine-detail");
                        DrawAdvancedFold();
                    }
                }
                Ui.Gap(0.6f);
                DrawPageFooter();
                Ui.Gap(0.4f);
            }
        }

        FileDialogs.Draw();

        if (dirty)
        {
            dirty = false;
            config.Save(PluginServices.PluginInterface);
        }
    }

    // ---------- the page most people see ----------

    /// <summary>
    /// The page reads as a short interview: every card asks one question in plain words and explains what
    /// the answer changes. The order is the order a player thinks in. What Gleam should do, what happens to
    /// junk, where it looks, what it needs, what it must never touch, and finally how much of this to show.
    /// </summary>
    private void DrawEssentials()
    {
        var p = Editing;

        using (Ui.Card("purpose"))
        {
            Ui.Ask("What would you like Gleam to do?", "Turn one off and Gleam stops offering it. You can turn it back on any time.");
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
            Ui.Ask("What should happen to the junk it finds?", "Gleam shows you the whole list first. Nothing leaves your bags until you press the button.");
            var preset = Presets.Detect(p.Thresholds);
            if (Ui.Segmented("##preset", ref preset, PresetOptions)) { p.ApplyPreset(preset); dirty = true; }
            Ui.Gap(0.25f);
            Ui.TextColoredWrapped(Ui.Cream, p.Thresholds.Policy.Describe());

            if (p.Thresholds.Policy == ActionPolicy.MarketListTradeable)
            {
                Ui.Gap(0.3f);
                ImGui.AlignTextToFramePadding();
                Ui.Hint("Put it up for sale in stacks of");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(70 * Ui.Scale);
                var per = config.MarketListStackSize;
                if (ImGui.InputInt("##mstack", ref per, 0, 0, "%d", ImGuiInputTextFlags.None)) { config.MarketListStackSize = Math.Clamp(per, 0, 9999); dirty = true; }
                Ui.Tooltip("Smaller lots sell faster. 0 puts the whole stack up at once.");
                ImGui.SameLine();
                Ui.Hint(per == 0 ? "(the whole stack)" : "at a time");
            }
        }

        if (config.AdvancedMode)
        using (Ui.Card("where"))
        {
            Ui.Ask("Where should Gleam look?", "It only ever opens the places ticked here.");
            DrawContainerChecks(p);
        }

        DrawRequiredPlugins();

        using (Ui.Card("protect")) protectEditor.Draw();
        // Shown whenever it holds anything, because what is on it is cleaned: a list that acts is never hidden.
        if (config.AdvancedMode || config.AlwaysDiscardList.Entries.Count > 0) using (Ui.Card("always")) alwaysEditor.Draw();

        if (config.AdvancedMode || config.Automation.CleanAfterVentures)
        using (Ui.Card("ventures"))
        {
            Ui.Ask("Should Gleam tidy up after your retainers?", "Needs the AutoRetainer plugin. It only discards, and only what the rules would tick on their own.");
            var a = config.Automation;
            var after = a.CleanAfterVentures;
            if (Ui.Check("Throw away junk a finished venture leaves in my bags", ref after)) { a.CleanAfterVentures = after; dirty = true; }
            ImGui.SameLine();
            if (AutoRetainer is null || !AutoRetainer.IsInstalled) Ui.Pill("not installed", Ui.Warn, null, "req:AutoRetainer"); else Ui.Pill("AutoRetainer", Ui.Ok, Dalamud.Interface.FontAwesomeIcon.Check, "req:AutoRetainer");
        }

        if (!config.AdvancedMode && HiddenChanges() is { Count: > 0 } hidden)
        using (Ui.Card("hidden"))
        {
            Ui.Ask("Some of your choices are hidden right now", $"{string.Join(". ", hidden)}. They still apply.");
            if (Ui.LinkButton("Show every setting")) { config.AdvancedMode = true; dirty = true; }
        }

        using (Ui.Card("finish"))
        {
            Ui.Ask("Anything to do once a run has finished?");
            var sortAfter = config.SortAfterRun;
            if (Ui.Check(ConfirmationWindow.SortAfterLabel, ref sortAfter)) { config.SortAfterRun = sortAfter; dirty = true; }
            Ui.Tooltip(ConfirmationWindow.SortAfterHint);
        }
    }

    /// <summary>
    /// Choices made in advanced mode that still apply once it is switched off. Hiding a setting is fine;
    /// hiding one that changes what Gleam does is not, so simple mode names them.
    /// </summary>
    private List<string> HiddenChanges()
    {
        var p = Editing;
        var list = new List<string>();
        var off = RuleEngine.AllRules.Count(r => !p.EnabledRules.Contains(r.Id));
        if (off > 0) list.Add($"{off} junk rule{(off == 1 ? " is" : "s are")} turned off");
        var closed = Enum.GetValues<ContainerKind>().Count(k => !p.IsContainerEnabled(k));
        if (closed > 0) list.Add($"Gleam may not look in {closed} place{(closed == 1 ? "" : "s")}");
        if (p.ExcludedRetainerIds.Count > 0) list.Add($"{p.ExcludedRetainerIds.Count} retainer{(p.ExcludedRetainerIds.Count == 1 ? " is" : "s are")} left alone");
        return list;
    }

    /// <summary>The containers Gleam may open, in a row that wraps rather than running off the card.</summary>
    private void DrawContainerChecks(Profile p)
    {
        var used = 0f;
        var room = ImGui.GetContentRegionAvail().X;
        foreach (var kind in Enum.GetValues<ContainerKind>())
        {
            var label = kind.DisplayName();
            var w = Ui.CheckWidth(label);
            if (used > 0f && used + ImGui.GetStyle().ItemSpacing.X + w <= room) { ImGui.SameLine(); used += ImGui.GetStyle().ItemSpacing.X + w; }
            else used = w;
            var on = p.IsContainerEnabled(kind);
            if (Ui.Check($"{label}##en{kind}", ref on)) { p.ContainerEnabled[kind] = on; dirty = true; }
        }
    }

    /// <summary>
    /// The last line of the page, and the only part of it that is not a setting: how much of the page you
    /// want to see, and the way out when something is broken. Both stay reachable in either mode.
    /// </summary>
    private void DrawPageFooter()
    {
        var adv = config.AdvancedMode;
        if (Ui.Check("Show me every setting", ref adv)) { config.AdvancedMode = adv; dirty = true; }
        Ui.Tooltip("Adds filters and sorting, a choice of action on every row, layouts and rules, and the rest of the settings. Off keeps Gleam to one list and one button.");

        ImGui.SameLine(0, 24 * Ui.Scale);
        var still = config.ReduceMotion;
        if (Ui.Check("Hold still", ref still)) { config.ReduceMotion = still; Ui.Reduced = still; dirty = true; }
        Ui.Tooltip("Turns off the fading, sliding and counting. Everything still works, it just arrives at once.");

        const string report = "Copy a bug report";
        const string link = "Something is not working";
        var style = ImGui.GetStyle();
        var linksWidth = ImGui.CalcTextSize(report, false, 0).X + ImGui.CalcTextSize(link, false, 0).X + style.FramePadding.X * 4 + style.ItemSpacing.X;
        Ui.RightAlignOrWrap(linksWidth, 260f * Ui.Scale);
        if (Ui.LinkButton(report)) CopyReport?.Invoke();
        Ui.Tooltip("Puts versions, settings, recent failures and the last self-test on your clipboard, ready to paste into a bug report. It holds no character or retainer names.");
        ImGui.SameLine();
        if (Ui.LinkButton(link)) openDebug();
        Ui.Tooltip("Opens the self-test and the log of what Gleam tried. Useful when a step keeps failing after a game update.");
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
            Ui.Ask("What Gleam needs to work", "Gleam walks to every bell, dresser, merchant and Grand Company a run needs, and vnavmesh does the walking. Lifestream lets it travel between towns; without it, Gleam does everything reachable from where you start.");

            Requirement("vnavmesh", haveNav, "Walks you to the bell, the dresser and the merchant.");
            Requirement("Lifestream", haveTravel, "Teleports you between towns and into an inn.", optional: true);

            if (!haveNav || !haveTravel)
            {
                Ui.Gap(0.4f);
                if (!haveNav)
                {
                    Ui.TextColored(Ui.Danger, "Install vnavmesh to let a run finish.");
                    Ui.HintWrapped("Without it Gleam only handles what you open yourself.");
                }
                else
                {
                    Ui.TextColored(Ui.Warn, "Install Lifestream to let Gleam travel.");
                    Ui.HintWrapped("Without it, start a run in an inn and Gleam manages from there.");
                }
            }

            // A patch this build was not checked on holds hands-free back until the player decides. Asked of
            // the game version itself, not of the pilot: cleaning after ventures is held back too, and it
            // needs no vnavmesh, so someone without it must still be able to go ahead.
            if (Pilot is not null && !Game.GameVersionGuard.AllowsUnattended(config))
            {
                Ui.Gap(0.4f);
                Ui.TextColored(Ui.Warn, "Hands-free is paused for this game patch.");
                Ui.HintWrapped($"The game was updated after this version of Gleam was checked: it was checked on {Game.GameVersionGuard.CheckedAgainst}, and you are on {Game.GameVersionGuard.Current()}. Runs you start by hand still work. An update to Gleam lifts the pause, or you can go ahead now.");
                if (Ui.LinkButton("Go ahead on this patch")) Pilot.GoAheadOnThisPatch();
                Ui.Tooltip(Ui.PatchGoAheadHint);
            }

            // Shown whenever it is on: it cleans without asking, so it is never tucked away behind advanced mode.
            if (config.AdvancedMode || config.Automation.UnseenRows != UnseenRowsMode.Skip)
            {
                Ui.Gap(0.4f);
                var a = config.Automation;
                var cleanUnseen = a.UnseenRows != UnseenRowsMode.Skip;
                if (Ui.Check("Also clean junk it only finds once it gets there", ref cleanUnseen)) { a.UnseenRows = cleanUnseen ? UnseenRowsMode.Clean : UnseenRowsMode.Skip; dirty = true; }
                Ui.Tooltip("A retainer or the dresser only shows what it holds once it is open. On: Gleam applies the same rules on the spot. Off: those items wait for your next review.");
            }
        }
    }

    /// <summary>
    /// One required plugin: its state first, then its name, then what Gleam uses it for. The sentence sits
    /// beside the name while there is room for it and drops underneath when the window is narrow, so the
    /// row never runs off the card.
    /// </summary>
    private static void Requirement(string name, bool installed, string what, bool optional = false)
    {
        var left = ImGui.GetCursorScreenPos().X;
        var room = ImGui.GetContentRegionAvail().X;
        if (installed) Ui.Pill("installed", Ui.Ok, Dalamud.Interface.FontAwesomeIcon.Check, $"req:{name}");
        // Missing a plugin Gleam can live without is a note, not an alarm.
        else if (optional) Ui.Pill("not installed", Ui.Warn, null, $"req:{name}");
        else Ui.Pill("missing", Ui.Danger, Dalamud.Interface.FontAwesomeIcon.ExclamationTriangle, $"req:{name}");
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        Ui.Text(name);

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var spare = room - (ImGui.GetItemRectMax().X - left) - spacing;
        if (ImGui.CalcTextSize(what, false, 0).X <= spare) { ImGui.SameLine(); Ui.Hint(what); }
        else { using var indent = ImRaii.PushIndent(6f, true, true); Ui.HintWrapped(what); }
    }

    // ---------- everything else, folded ----------

    private void DrawAdvancedFold()
    {
        Ui.Gap(0.4f);
        Fold("What Gleam treats as junk", DrawRules);
        Fold("Which retainers it may use", DrawContainers);
        Fold("When Gleam speaks up", DrawNotifications);
        Fold("Other plugins", DrawIntegrations);
    }

    private static void Fold(string title, Action body) => Ui.Fold(title, body);
}
