using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using TidyUp.Core.Model;
using TidyUp.Core.Planning;
using TidyUp.Core.Rules;
using TidyUp.Game;
using TidyUp.Services;

namespace TidyUp.Windows;

/// <summary>
/// The contract window. One search box, one list, one button. Filters and sorting live behind a
/// single menu so the default view is nothing but the items and why they were picked.
/// </summary>
public sealed class ConfirmationWindow : StyledWindow
{
    private readonly RunCoordinator coordinator;
    private readonly IconCache icons;
    private readonly ItemDatabase db;
    private readonly Configuration config;
    private readonly IGamepadState gamepad;

    /// <summary>Set by the plugin when hands-free mode is available.</summary>
    public Automation.AutoPilot? Pilot { get; set; }

    /// <summary>The other pages of this window; set by the plugin.</summary>
    public OrganizerPanel? Organizer { get; set; }
    public HistoryWindow? History { get; set; }
    public SettingsWindow? SettingsPage { get; set; }

    /// <summary>Which half is showing. A running job pulls the window to its own half.</summary>
    internal Ui.AppMode Mode { get; private set; } = Ui.AppMode.Clean;

    /// <summary>Opens the window on the given half.</summary>
    /// <summary>Where /gleam lands: the half the player actually asked for.</summary>
    internal Ui.AppMode HomePage => config.StartOnOrganize ? Ui.AppMode.Organize : Ui.AppMode.Clean;

    internal void Show(Ui.AppMode mode)
    {
        Mode = mode;
        IsOpen = true;
        switch (mode)
        {
            case Ui.AppMode.Organize: Organizer?.OnShown(); break;
            case Ui.AppMode.History: History?.OnShown(); break;
            case Ui.AppMode.Settings: break;
            default:
                if (coordinator.CurrentPlan is null && !coordinator.IsRunning) _ = coordinator.RefreshPlanAsync(openWindow: false);
                break;
        }
    }

    private string search = string.Empty;
    private ContainerKind? filterContainer;
    private string? filterRule;
    private ActionKind? filterAction;
    private readonly HashSet<ItemTag> filterTags = new();
    /// <summary>null = both, true = tradeable only, false = untradeable only.</summary>
    private bool? filterTradeable;
    private enum SortKey { Name, Quantity, Action, Market }
    /// <summary>Sort applied from the Filter menu to every section.</summary>
    private SortKey sortKey = SortKey.Name;
    /// <summary>+1 ascending, -1 descending. "None" is Name ascending, the natural order.</summary>
    private int sortDir = 1;
    private bool SortIsDefault => sortKey == SortKey.Name && sortDir == 1;
    /// <summary>Per-section sort set by clicking a column title; wins over the global one for that section.</summary>
    private readonly Dictionary<string, (SortKey Key, int Dir)> sectionSort = new();

    private readonly record struct HeaderColumn(float X, float Width, string Label, SortKey? Key, bool Numeric);
    private readonly Dictionary<string, double> rowFlash = new();
    private object? staggerPlan;
    private double staggerAt;

    /// <summary>
    /// Rows fade in one after another when a fresh list arrives, so the eye follows it down the page. Only
    /// the first screenful is staggered: a list of three hundred must not take three seconds to appear.
    /// </summary>
    private float RowAlpha(int index)
    {
        var elapsed = ImGui.GetTime() - staggerAt - Math.Min(index, 12) * 0.011;
        return Ui.EaseOut((float)Math.Clamp(elapsed / 0.16, 0, 1));
    }

    /// <summary>The simple layer: no filters, no per-row choices, plain words. Advanced adds everything back.</summary>
    private bool Simple => !config.AdvancedMode;

    /// <summary>The one way a row gets ticked or unticked: keeps the session skip in step and gives the row a brief glow.</summary>
    private void SetChecked(PlanRow row, bool on)
    {
        if (row.Checked == on) return;
        row.Checked = on;
        if (on) coordinator.SessionSkips.Remove(row.Key); else coordinator.SessionSkips.Add(row.Key);
        rowFlash[row.Key] = ImGui.GetTime();
    }
    private bool capArmed;
    private int cursor = -1;
    private readonly List<PlanRow> visibleRows = new();
    private readonly Dictionary<string, bool> sectionOpen = new();

    public ConfirmationWindow(RunCoordinator coordinator, IconCache icons, ItemDatabase db, Configuration config, IGamepadState gamepad)
        : base("Gleam###TidyUpConfirm")
    {
        this.coordinator = coordinator;
        this.icons = icons;
        this.db = db;
        this.config = config;
        this.gamepad = gamepad;
        Size = new Vector2(860, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(560, 320), MaximumSize = new Vector2(4000, 3000) };
        AddNav(FontAwesomeIcon.History, "What Gleam did", () => Show(Ui.AppMode.History));
        AddNav(FontAwesomeIcon.Cog, "Settings", () => Show(Ui.AppMode.Settings));
    }

    public override void OnOpen()
    {
        base.OnOpen();
        capArmed = false;
        cursor = -1;
        if (Mode == Ui.AppMode.Organize) Organizer?.OnShown();
        else if (coordinator.CurrentPlan is null && !coordinator.IsRunning)
            _ = coordinator.RefreshPlanAsync(openWindow: false);
    }

    public override void Draw()
    {
        // Whatever is running owns the window.
        if (Pilot is { IsRunning: true }) Mode = Pilot.Mode == Automation.PilotMode.Organize ? Ui.AppMode.Organize : Ui.AppMode.Clean;
        else if (coordinator.IsRunning) Mode = Ui.AppMode.Clean;
        using var page = Ui.PageTransition(Mode);
        if (Mode == Ui.AppMode.Organize && Organizer is not null) { Organizer.Draw(); return; }
        if (Mode == Ui.AppMode.History && History is not null) { History.Draw(); return; }
        if (Mode == Ui.AppMode.Settings && SettingsPage is not null) { SettingsPage.Draw(); return; }

        var plan = coordinator.CurrentPlan;
        if (Pilot is { IsRunning: true, Mode: Automation.PilotMode.Clean }) { DrawPilotRunning(); return; }
        if (coordinator.IsRunning) { DrawRunning(); return; }
        if (plan is null)
        {
            Ui.RunningHeader(icons.LogoMedium, "Looking through your things…", string.IsNullOrEmpty(coordinator.Status) ? null : coordinator.Status);
            Ui.Gap(0.8f);
            var w = ImGui.GetWindowWidth() * 0.5f;
            ImGui.SetCursorPosX((ImGui.GetWindowWidth() - w) / 2);
            Ui.ProgressBar("scan", null, w);
            return;
        }
        if (!config.SeenFirstRun) { DrawFirstRun(plan); return; }

        DrawTopBar(plan);
        if (!config.SeenCleanIntro && !Simple)
        {
            Ui.Gap(0.3f);
            if (Ui.Banner(Ui.Info, "New here?", "Gleam lists what it thinks is junk. Nothing happens until you press Clean, and you can untick anything.", dismissLabel: "Got it"))
            {
                config.SeenCleanIntro = true;
                config.Save(PluginServices.PluginInterface);
            }
        }
        DrawLastRunBanner();
        DrawPilotErrorBanner();
        DrawOrganizeOffer();
        Ui.Gap(0.5f);

        var footer = ImGui.GetFrameHeight() * 2.4f + Ui.Space;
        using (var child = ImRaii.Child("##rows", new Vector2(0, -footer), false, ImGuiWindowFlags.None))
        {
            if (child)
            {
                visibleRows.Clear();
                if (!ReferenceEquals(staggerPlan, plan)) { staggerPlan = plan; staggerAt = ImGui.GetTime(); }
                var drewAny = Simple ? DrawSimpleGroups(plan) : DrawContainerSections(plan);
                if (!Simple && coordinator.FocusContainer is null && plan.Alts.Count > 0) DrawAlts(plan);
                if (!drewAny)
                {
                    if (plan.AllRows.Any()) Ui.EmptyState(icons.LogoMedium, "Nothing matches your search.", Simple ? "Clear the search box to see everything." : "Clear a chip or the search box to see more.");
                    else
                    {
                        Ui.EmptyState(icons.LogoMedium, "Nothing to clean.", "Everything looks tidy.");
                        if (Organizer is not null)
                        {
                            Ui.Gap(0.5f);
                            var label = "Organize instead";
                            ImGui.SetCursorPosX(Math.Max(0, (ImGui.GetWindowWidth() - ImGui.CalcTextSize(label, false, 0).X - 16 * Ui.Scale) / 2));
                            if (Ui.LinkButton(label)) Show(Ui.AppMode.Organize);
                        }
                    }
                }
                if (Simple) DrawOutOfReachNote(plan); else DrawExcludedNote(plan);
            }
        }

        HandleKeyboard(plan);
        DrawFooter(plan);
    }

    private bool bannerDismissed;
    private Core.Execution.RunReport? bannerReport;
    private string? pilotErrorShown;

    /// <summary>Why the last hands-free run stopped, once, until dismissed or a new run starts.</summary>
    private void DrawPilotErrorBanner()
    {
        if (Pilot is not { Mode: Automation.PilotMode.Clean, LastError: { } error } || Pilot.IsRunning) return;
        if (pilotErrorShown == error) return;
        Ui.Gap(0.3f);
        if (Ui.Banner(Ui.Warn, "Hands-free stopped", error)) pilotErrorShown = error;
    }

    /// <summary>One quiet line after a run: what happened and what is still waiting. Dismissed with a click.</summary>
    /// <summary>After the first clean, offer the other half once. Simple mode hides Organize until this is answered.</summary>
    private void DrawOrganizeOffer()
    {
        if (!Simple || Organizer is null || config.UseOrganize || !config.HasCleanedOnce || config.AnsweredOrganizeOffer) return;
        Ui.Gap(0.3f);
        if (Ui.Banner(Ui.Accent, "One more thing", "Gleam can also put away what you keep. Materia in the saddlebag, spare gear with a retainer, that sort of thing.",
                dismissLabel: "No thanks", link: ("Show me", () =>
                {
                    config.UseOrganize = true;
                    config.AnsweredOrganizeOffer = true;
                    config.Save(PluginServices.PluginInterface);
                    Show(Ui.AppMode.Organize);
                })))
        {
            config.AnsweredOrganizeOffer = true;
            config.Save(PluginServices.PluginInterface);
        }
    }

    private void DrawLastRunBanner()
    {
        var report = coordinator.LastReport;
        if (report is null) return;
        if (!ReferenceEquals(report, bannerReport)) { bannerReport = report; bannerDismissed = false; }
        if (bannerDismissed) return;

        var parts = new List<string>();
        if (report.Done > 0) parts.Add($"cleaned {report.Done}");
        if (report.Skipped > 0) parts.Add($"skipped {report.Skipped} that changed");
        if (report.Failed > 0) parts.Add($"{report.Failed} failed");
        foreach (var (reason, count) in report.PendingByReason())
            parts.Add($"{count} waiting: {(string.IsNullOrEmpty(reason) ? "open their container" : reason)}");
        if (parts.Count == 0) return;

        Ui.Gap(0.3f);
        var color = report.Failed > 0 ? Ui.Warn : Ui.Ok;
        if (Ui.Banner(color, "Last run", string.Join(" · ", parts), link: ("See what happened", () => Show(Ui.AppMode.History)))) bannerDismissed = true;
    }

    // ---------- top bar: search, filter menu, rescan ----------

    private static readonly IReadOnlyList<(Core.Rules.PresetName, string)> PresetOptions =
    [
        (Core.Rules.PresetName.MarketBoard, Core.Rules.PresetName.MarketBoard.Label()),
        (Core.Rules.PresetName.Vendor, Core.Rules.PresetName.Vendor.Label()),
        (Core.Rules.PresetName.DiscardAll, Core.Rules.PresetName.DiscardAll.Label()),
    ];

    private void DrawTopBar(RunPlan plan)
    {
        // Header: who we are and how much is on the table; the preset sits on the same row because what
        // this list will do is the most important thing on the screen.
        var profile = coordinator.EffectiveProfile;
        var preset = Core.Rules.Presets.Detect(profile.Thresholds);
        var checkedCount = plan.AllRows.Count(r => r.Checked && r.IsExecutable);
        var total = plan.AllRows.Count(r => r.IsExecutable);
        var containers = plan.Sections.Count(s => s.Rows.Count > 0);
        var subtitle = coordinator.FocusContainer is { } fc
            ? $"Only the {FocusName(plan, fc)} · {checkedCount} of {total} selected"
            : $"{total} item{(total == 1 ? "" : "s")} in {containers} container{(containers == 1 ? "" : "s")} · {checkedCount} selected";
        if (Simple)
        {
            var summary = plan.Summarize();
            var freed = (int)Ui.Count("freed", summary.SlotsFreedByContainer.Values.Sum());
            var worth = Ui.Count("worth", summary.GilRecovered + summary.MarketGil);
            var found = (int)Ui.Count("found", total);
            var sentence = total == 0 ? "Nothing looks like junk right now."
                : $"Gleam found {found} item{(found == 1 ? "" : "s")} of junk." + (freed > 0 ? $" Cleaning them frees {freed} slot{(freed == 1 ? "" : "s")}" : string.Empty)
                  + (worth > 0 ? $"{(freed > 0 ? " and recovers" : " Cleaning them recovers")} about {Ui.Gil(worth)}." : freed > 0 ? "." : string.Empty);
            var offerOrganize = Organizer is not null && config.UseOrganize;
            Ui.Header(icons.LogoSmall, "Gleam", sentence, 0f, null, null, !offerOrganize ? null : () => { if (Ui.ModeSwitch(Ui.AppMode.Clean)) Show(Ui.AppMode.Organize); });
            ImGui.AlignTextToFramePadding();
            Ui.Hint(profile.Thresholds.Policy.Describe());
            ImGui.SameLine();
            if (Ui.LinkButton("Change")) Show(Ui.AppMode.Settings);
            Ui.Tooltip("Opens Settings, where you choose what happens to junk.");
            if (coordinator.FocusContainer is not null)
            {
                ImGui.SameLine();
                if (Ui.LinkButton("Show all containers")) _ = coordinator.RefreshPlanAsync(openWindow: false);
            }
            Ui.Gap(0.4f);
            DrawSimpleToolbar(plan, checkedCount);
            return;
        }
        Ui.Header(icons.LogoSmall, "Gleam", subtitle, Ui.SegmentedWidth(PresetOptions), () =>
        {
            if (Ui.Segmented("##preset", ref preset, PresetOptions))
            {
                if (config.Profiles.IsOverridden(plan.CharacterId, nameof(Core.Lists.Profile.Thresholds)))
                    config.Profiles.GetOrCreateOverride(plan.CharacterId, plan.CharacterName).Values.ApplyPreset(preset);
                else
                    config.Profiles.Account.ApplyPreset(preset);
                config.Save(PluginServices.PluginInterface);
                _ = coordinator.RefreshPlanAsync(openWindow: false, coordinator.FocusContainer);
            }
            Ui.Tooltip("Sell on market board: lists marketable items through your retainers at the lowest price on your home world, sells other tradeable items to a retainer, discards untradeable ones.\nSell to vendors: sells tradeable items to a retainer, discards untradeable ones.\nDiscard all: discards everything proposed.");
        }, profile.Thresholds.Policy.Describe(), Organizer is null ? null : () => { if (Ui.ModeSwitch(Ui.AppMode.Clean)) Show(Ui.AppMode.Organize); });

        if (coordinator.FocusContainer is not null)
        {
            Ui.Hint("Showing one container.");
            ImGui.SameLine();
            if (Ui.LinkButton("Show all")) _ = coordinator.RefreshPlanAsync(openWindow: false);
        }

        Ui.Gap(0.4f);

        DrawContainerChips(plan);
        DrawTypeChips(plan);

        Ui.SearchBox("##search", ref search, 260 * Ui.Scale);

        ImGui.SameLine();
        var filtersActive = filterContainer is not null || filterRule is not null || filterAction is not null || filterTags.Count > 0 || filterTradeable is not null || !SortIsDefault;
        if (Ui.IconButton(FontAwesomeIcon.Filter, filtersActive ? "Filter •" : "Filter")) ImGui.OpenPopup("##filters", ImGuiPopupFlags.None);
        DrawFilterMenu();

        ImGui.SameLine();
        if (Ui.IconButton(FontAwesomeIcon.Sync, "Refresh")) _ = coordinator.RefreshPlanAsync(openWindow: false, coordinator.FocusContainer);
        Ui.Tooltip("Looks through your containers again.");

        ImGui.SameLine();
        Ui.RightAlign(90 * Ui.Scale);
        // All means all of what is on screen: with a type, container or search filter on, it ticks just
        // those rows. The soft cap still asks for a second click on a big run.
        var narrowed = filterContainer is not null || filterRule is not null || filterAction is not null || filterTags.Count > 0 || filterTradeable is not null || !string.IsNullOrWhiteSpace(search);
        var executable = (narrowed ? Filter(plan.AllRows) : plan.AllRows).Where(r => r.IsExecutable).ToList();
        var allChecked = executable.Count > 0 && executable.All(r => r.Checked);
        if (Ui.LinkButton(allChecked ? "Clear" : narrowed ? "Select shown" : "Select all"))
        {
            foreach (var r in executable) SetChecked(r, !allChecked);
        }
        var warned = executable.Count(r => r.Proposal.Warnings.Count > 0);
        var scope = narrowed ? "every row that matches the current filters" : "every row";
        Ui.Tooltip(allChecked ? "Unticks every row." : warned > 0
            ? $"Ticks {scope}, including {warned} with a warning. Glance at those first."
            : $"Ticks {scope}.");
    }

    /// <summary>One chip per container with its row count. Click to show only that container; click again for all.</summary>
    /// <summary>Filter chips earn their place only once the list is long enough to need narrowing.</summary>
    private const int ChipsFromRows = 12;

    /// <summary>Simple layer: search only when the list is long, a Look again button, and Select all.</summary>
    private void DrawSimpleToolbar(RunPlan plan, int checkedCount)
    {
        var total = plan.AllRows.Count();
        if (total > 40) { Ui.SearchBox("##search", ref search, 260 * Ui.Scale); ImGui.SameLine(); }
        if (Ui.IconButton(FontAwesomeIcon.Sync, "Look again")) _ = coordinator.RefreshPlanAsync(openWindow: false, coordinator.FocusContainer);
        Ui.Tooltip("Looks through your containers again.");
        ImGui.SameLine();
        Ui.RightAlign(90 * Ui.Scale);
        var executable = Filter(plan.AllRows).Where(r => r.IsExecutable).ToList();
        var allChecked = executable.Count > 0 && executable.All(r => r.Checked);
        if (Ui.LinkButton(allChecked ? "Clear" : "Select all"))
            foreach (var r in executable) SetChecked(r, !allChecked);
        Ui.Tooltip(allChecked ? "Unticks every row." : "Ticks every row, including the ones Gleam was unsure about.");
    }

    private void DrawContainerChips(RunPlan plan)
    {
        if (coordinator.FocusContainer is not null || plan.AllRows.Count() < ChipsFromRows) return;
        var groups = plan.Sections
            .GroupBy(s => s.Kind)
            .OrderBy(g => g.Key.ExecutionOrder())
            .Select(g => (Kind: g.Key, Rows: g.Sum(s => s.Rows.Count), Checked: g.Sum(s => s.CheckedCount)))
            .ToList();
        if (groups.Count == 0) return;

        Ui.Hint("Containers");
        ImGui.SameLine();
        foreach (var (kind, rows, checkedCount) in groups)
        {
            var active = filterContainer == kind;
            var label = checkedCount > 0 ? $"{kind.DisplayName()} {checkedCount}/{rows}" : $"{kind.DisplayName()} {rows}";
            if (Ui.Chip(label, active)) filterContainer = active ? null : kind;
            Ui.Tooltip(active ? "Showing only this container. Click to show all." : $"Show only the {kind.DisplayName().ToLowerInvariant()}.");
            ImGui.SameLine();
        }
        ImGui.NewLine();
        Ui.Gap(0.2f);
    }

    /// <summary>Item-type chips. Several can be on at once; none on means every type.</summary>
    private void DrawTypeChips(RunPlan plan)
    {
        if (plan.AllRows.Count() < ChipsFromRows) return;
        var rows = plan.AllRows;
        if (coordinator.FocusContainer is { } focus) rows = rows.Where(r => r.Item.Slot.Kind == focus);
        var groups = rows
            .GroupBy(r => ItemTags.Of(r.Info))
            .OrderBy(g => g.Key)
            .Select(g => (Tag: g.Key, Rows: g.Count(), Checked: g.Count(r => r.Checked)))
            .ToList();
        var rowList = rows.ToList();
        var hasTradeSplit = rowList.Any(r => r.Info.IsUntradable) && rowList.Any(r => !r.Info.IsUntradable);
        if (groups.Count < 2 && !hasTradeSplit) return;

        Ui.Hint("Types");
        ImGui.SameLine();
        foreach (var (tag, count, checkedCount) in groups)
        {
            var active = filterTags.Contains(tag);
            var label = checkedCount > 0 ? $"{tag.Label()} {checkedCount}/{count}" : $"{tag.Label()} {count}";
            if (Ui.Chip(label, active))
            {
                if (!filterTags.Remove(tag)) filterTags.Add(tag);
            }
            Ui.Tooltip(active ? "Click to stop filtering by this type." : $"Show {tag.Label().ToLowerInvariant()} only. Click more types to add them.");
            ImGui.SameLine();
        }
        var tradeable = rowList.Count(r => !r.Info.IsUntradable);
        var untradeable = rowList.Count(r => r.Info.IsUntradable);
        if (tradeable > 0 && untradeable > 0)
        {
            ImGui.TextDisabled("|");
            ImGui.SameLine();
            if (Ui.Chip($"Tradeable {tradeable}", filterTradeable == true)) filterTradeable = filterTradeable == true ? null : true;
            Ui.Tooltip("Only items that can be sold or traded.");
            ImGui.SameLine();
            if (Ui.Chip($"Untradeable {untradeable}", filterTradeable == false)) filterTradeable = filterTradeable == false ? null : false;
            Ui.Tooltip("Only items that cannot be sold or traded. Discarding is the only way out for these.");
            ImGui.SameLine();
        }
        if ((filterTags.Count > 0 || filterTradeable is not null) && Ui.LinkButton("Clear")) { filterTags.Clear(); filterTradeable = null; }
        ImGui.NewLine();
        Ui.Gap(0.2f);
    }

    private void DrawFilterMenu()
    {
        using var popup = ImRaii.Popup("##filters");
        if (!popup) return;

        // Only what this list actually holds, with counts. A menu full of entries that match nothing is
        // a menu nobody trusts, and the column titles already do the sorting.
        var plan = coordinator.CurrentPlan;
        var rows = plan?.AllRows.ToList() ?? new List<PlanRow>();

        Ui.Hint("Why it is here");
        if (ImGui.MenuItem("Anything", string.Empty, filterRule is null, true)) filterRule = null;
        foreach (var g in rows.GroupBy(r => r.Proposal.RuleId).OrderByDescending(g => g.Count()))
        {
            var name = g.Key switch
            {
                "always-discard" => "On your Always junk list",
                "manual" => "Nothing suggested it",
                _ => Core.Rules.RuleEngine.AllRules.FirstOrDefault(r => r.Id == g.Key)?.Name ?? g.Key,
            };
            if (ImGui.MenuItem($"{name}  ({g.Count()})", string.Empty, filterRule == g.Key, true)) filterRule = g.Key;
        }

        ImGui.Separator();
        Ui.Hint("What will happen");
        if (ImGui.MenuItem("Anything", string.Empty, filterAction is null, true)) filterAction = null;
        foreach (var g in rows.Where(r => r.IsExecutable).GroupBy(r => r.ChosenAction).OrderByDescending(g => g.Count()))
            if (ImGui.MenuItem($"{g.Key.Label()}  ({g.Count()})", string.Empty, filterAction == g.Key, true)) filterAction = g.Key;

        ImGui.Separator();
        if (ImGui.MenuItem("Clear all filters", string.Empty, false, true)) { filterContainer = null; filterRule = null; filterAction = null; filterTags.Clear(); filterTradeable = null; sortKey = SortKey.Name; sortDir = 1; sectionSort.Clear(); }
    }

    private IEnumerable<PlanRow> Filter(IEnumerable<PlanRow> rows, string? sectionKey = null)
    {
        var (key, dir) = sectionKey is not null && sectionSort.TryGetValue(sectionKey, out var own) ? own : (sortKey, sortDir);
        var q = rows;
        if (filterContainer is { } c) q = q.Where(r => r.Item.Slot.Kind == c);
        if (filterRule is { } rule) q = q.Where(r => r.Proposal.RuleId == rule);
        if (filterAction is { } a) q = q.Where(r => r.ChosenAction == a);
        if (filterTags.Count > 0) q = q.Where(r => filterTags.Contains(ItemTags.Of(r.Info)));
        if (filterTradeable is { } tradeOnly) q = q.Where(r => r.Info.IsUntradable != tradeOnly);
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(r => r.Info.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || r.Proposal.Reason.Contains(search, StringComparison.OrdinalIgnoreCase));
        IOrderedEnumerable<PlanRow> ordered = key switch
        {
            SortKey.Quantity => dir > 0 ? q.OrderBy(r => r.Item.Quantity) : q.OrderByDescending(r => r.Item.Quantity),
            SortKey.Action => dir > 0 ? q.OrderBy(r => r.ChosenAction.Label()) : q.OrderByDescending(r => r.ChosenAction.Label()),
            SortKey.Market => dir > 0 ? q.OrderBy(r => r.Proposal.MarketUnitPrice) : q.OrderByDescending(r => r.Proposal.MarketUnitPrice),
            _ => dir > 0 ? q.OrderBy(r => r.Info.Name, StringComparer.OrdinalIgnoreCase) : q.OrderByDescending(r => r.Info.Name, StringComparer.OrdinalIgnoreCase),
        };
        return key == SortKey.Name ? ordered : ordered.ThenBy(r => r.Info.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Click cycles ascending, descending, then back to the natural order, for this section only.</summary>
    private void ClickSort(string sectionKey, SortKey key, bool numeric)
    {
        var (curKey, curDir) = sectionSort.TryGetValue(sectionKey, out var own) ? own : (SortKey.Name, 1);
        if (curKey != key || !sectionSort.ContainsKey(sectionKey)) { sectionSort[sectionKey] = (key, numeric ? -1 : 1); return; }
        var second = numeric ? 1 : -1;
        if (curDir != second) sectionSort[sectionKey] = (key, second);
        else sectionSort.Remove(sectionKey);
    }

    /// <summary>One column title: muted, clickable when sortable, with a small arrow when it is the active sort.</summary>
    private void DrawHeaderTitle(string sectionKey, HeaderColumn col, string idSuffix)
    {
        if (col.Key is null) { ImGui.AlignTextToFramePadding(); Ui.Hint(col.Label); return; }
        var (curKey, curDir) = sectionSort.TryGetValue(sectionKey, out var own) ? own : (SortKey.Name, 1);
        var isSorted = sectionSort.ContainsKey(sectionKey) && curKey == col.Key;
        using (ImRaii.PushColor(ImGuiCol.Text, isSorted ? Ui.AccentSoft : Ui.Muted))
        using (ImRaii.PushColor(ImGuiCol.HeaderHovered, new Vector4(1, 1, 1, 0.06f)))
        using (ImRaii.PushColor(ImGuiCol.HeaderActive, new Vector4(1, 1, 1, 0.09f)))
        {
            if (ImGui.Selectable($"{col.Label}##hdr{col.Key}{idSuffix}", false, ImGuiSelectableFlags.None, new Vector2(col.Width, 0)))
                ClickSort(sectionKey, col.Key.Value, col.Numeric);
        }
        Ui.Tooltip(isSorted
            ? (curDir > 0 ? "Sorted ascending. Click for descending." : "Sorted descending. Click to clear.")
            : $"Sort this section by {col.Label.ToLowerInvariant()}.");
        if (!isSorted) return;

        // Arrow right after the title, inside the same cell.
        var min = ImGui.GetItemRectMin();
        var h = ImGui.GetItemRectSize().Y;
        var icon = (curDir > 0 ? FontAwesomeIcon.SortUp : FontAwesomeIcon.SortDown).ToIconString();
        var textW = ImGui.CalcTextSize(col.Label, false, 0).X;
        using var f = ImRaii.PushFont(UiBuilder.IconFont);
        var iconH = ImGui.GetTextLineHeight();
        ImGui.GetWindowDrawList().AddText(new Vector2(min.X + textW + 8 * Ui.Scale, min.Y + (h - iconH) / 2), ImGui.GetColorU32(Ui.AccentSoft), icon);
    }

    /// <summary>
    /// When a section's table runs past the top of the scrolling list, its column titles are re-drawn
    /// pinned to the top edge so they stay readable while scrolling through that section.
    /// </summary>
    private void DrawStickyHeader(string sectionKey, IReadOnlyList<HeaderColumn> cols, Vector2 tableMin, Vector2 tableMax)
    {
        var headerH = 24 * Ui.Scale;
        var top = ImGui.GetWindowPos().Y;
        if (tableMin.Y >= top || tableMax.Y <= top + headerH * 2) return;

        var a = Ui.Appear($"sticky:{sectionKey}", 0.16f);
        var left = ImGui.GetWindowPos().X;
        var right = left + ImGui.GetWindowWidth();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(new Vector2(left, top), new Vector2(right, top + headerH), ImGui.GetColorU32(Ui.Ink with { W = a }));
        dl.AddLine(new Vector2(left, top + headerH), new Vector2(right, top + headerH), ImGui.GetColorU32(Ui.InkLine * new Vector4(1, 1, 1, a)), 1f);

        var saved = ImGui.GetCursorScreenPos();
        using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, a))
        using (ImRaii.PushId($"sticky{sectionKey}"))
        {
            foreach (var col in cols)
            {
                ImGui.SetCursorScreenPos(new Vector2(col.X, top + 2 * Ui.Scale));
                DrawHeaderTitle(sectionKey, col, "s");
            }
        }
        ImGui.SetCursorScreenPos(saved);
    }

    // ---------- sections ----------

    /// <summary>Advanced: one collapsing section per container, as the run itself is organised.</summary>
    private bool DrawContainerSections(RunPlan plan)
    {
        var sections = coordinator.FocusContainer is { } f ? plan.Sections.Where(s => s.Kind == f) : plan.Sections;
        var drewAny = false;
        foreach (var section in sections.OrderBy(s => s.Kind.ExecutionOrder()).ThenBy(s => s.OwnerName))
        {
            var rows = Filter(section.Rows, $"{section.Kind}:{section.OwnerId}").ToList();
            if (rows.Count == 0) continue;
            drewAny = true;
            DrawSection(section, rows);
        }
        return drewAny;
    }

    /// <summary>
    /// Simple: one card per outcome, so the screen reads "sell these, throw these away" instead of listing
    /// seven containers. Only what can be reached now is listed; the rest is one line underneath.
    /// </summary>
    private bool DrawSimpleGroups(RunPlan plan)
    {
        var handsFree = Pilot is not null && config.Automation.Enabled;
        var reachable = plan.Sections.Where(s => handsFree || s.IsAvailableNow).SelectMany(s => s.Rows);
        if (coordinator.FocusContainer is { } f) reachable = plan.Sections.Where(s => s.Kind == f).SelectMany(s => s.Rows);
        var groups = Filter(reachable)
            .GroupBy(r => r.ChosenAction)
            .OrderBy(g => g.Key == ActionKind.Discard ? 1 : 0)
            .ThenBy(g => g.Key.Label(), StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var group in groups) DrawOutcomeGroup(group.Key, group.ToList());
        return groups.Count > 0;
    }

    /// <summary>One outcome card: a tick for the whole group, what it does, what it is worth, and the items inside.</summary>
    private void DrawOutcomeGroup(ActionKind action, List<PlanRow> rows)
    {
        var key = $"grp:{action}";
        if (!sectionOpen.TryGetValue(key, out var open)) open = false;
        var checkedHere = rows.Count(r => r.Checked);
        var value = rows.Where(r => r.Checked).Sum(r => r.Proposal.ValueGil);
        var word = action switch
        {
            ActionKind.Discard => "Throw away",
            ActionKind.VendorSell => "Sell to a vendor",
            ActionKind.MarketList => "Sell on the market board",
            ActionKind.ExpertDelivery => "Turn in for seals",
            ActionKind.Desynth => "Break down",
            _ => action.Label(),
        };

        using (Ui.Card(key))
        {
            // A tick for the whole group is the only control most players will ever need.
            var all = checkedHere == rows.Count;
            var some = checkedHere > 0 && !all;
            var box = all;
            using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * (some ? 0.7f : 1f)))
            {
                if (Ui.Check($"##all{key}", ref box))
                    foreach (var r in rows) SetChecked(r, box);
            }
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            Ui.TextColored(Ui.ActionColor(action), word);
            ImGui.SameLine();
            Ui.Text($"{rows.Count} item{(rows.Count == 1 ? "" : "s")}");
            var shown = Ui.Count($"grpval:{action}", value);
            // The worth is the first thing to go when the card is narrow; the link and the count are not.
            var linkLabel = open ? "Hide the list" : "See the list";
            var linkW = ImGui.CalcTextSize(linkLabel, false, 0).X + ImGui.GetStyle().FramePadding.X * 2 + 8 * Ui.Scale;
            if (shown > 0 && ImGui.GetContentRegionAvail().X > linkW + 120 * Ui.Scale)
            {
                ImGui.SameLine();
                Ui.TextColored(Ui.Market, $"about {Ui.Gil(shown)}");
            }
            ImGui.SameLine();
            Ui.RightAlign(linkW);
            if (Ui.LinkButton(linkLabel)) sectionOpen[key] = !open;

            if (!open) return;
            Ui.Gap(0.3f);
            using var fade = ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * Ui.Appear($"open:{key}", 0.18f));
            using var table = ImRaii.Table($"##t{key}", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.PadOuterX);
            if (!table) return;
            ImGui.TableSetupColumn("##chk", ImGuiTableColumnFlags.WidthFixed, 24 * Ui.Scale, 0);
            ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, 30 * Ui.Scale, 0);
            ImGui.TableSetupColumn("##item", ImGuiTableColumnFlags.WidthStretch, 5f, 0);
            ImGui.TableSetupColumn("##market", ImGuiTableColumnFlags.WidthFixed, 96 * Ui.Scale, 0);
            foreach (var row in rows.OrderBy(r => r.Info.Name, StringComparer.OrdinalIgnoreCase))
            {
                var index = visibleRows.Count;
                visibleRows.Add(row);
                DrawGroupRow(row, index);
            }
        }
    }

    private void DrawGroupRow(PlanRow row, int index)
    {
        using var id = ImRaii.PushId(row.Key);
        using var fade = ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * RowAlpha(index));
        ImGui.TableNextRow(ImGuiTableRowFlags.None, 30 * Ui.Scale);
        var glow = rowFlash.TryGetValue(row.Key, out var at) ? (float)Math.Clamp(1 - (ImGui.GetTime() - at) / 0.7, 0, 1) : 0f;
        if (glow > 0f) ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(Ui.Accent * new Vector4(1, 1, 1, 0.22f * glow * glow)));

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        var chk = row.Checked;
        if (Ui.Check("##c", ref chk, !row.IsExecutable)) SetChecked(row, chk);

        ImGui.TableNextColumn();
        Ui.ImageRounded(icons.Get(row.Info.IconId, row.Item.IsHq), new Vector2(26 * Ui.Scale, 26 * Ui.Scale), 4 * Ui.Scale);

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        if (ImGui.Selectable(row.Info.Name + (row.Item.IsHq ? " " : ""), false, ImGuiSelectableFlags.AllowItemOverlap, Vector2.Zero) && row.IsExecutable)
            SetChecked(row, !row.Checked);
        if (ImGui.IsItemHovered()) DrawRowTooltip(row);
        DrawRowContextMenu(row);
        if (row.Item.Quantity > 1) { ImGui.SameLine(); Ui.Hint($"× {row.Item.Quantity}"); }

        ImGui.TableNextColumn();
        DrawMarketPrice(row);
        _ = index;
    }

    /// <summary>Simple: everything that cannot be reached from here, in one line rather than dead sections.</summary>
    private void DrawOutOfReachNote(RunPlan plan)
    {
        var handsFree = Pilot is not null && config.Automation.Enabled;
        if (handsFree || coordinator.FocusContainer is not null) return;
        var away = plan.Sections.Where(s => !s.IsAvailableNow && s.Rows.Count > 0).ToList();
        if (away.Count == 0) return;
        var total = away.Sum(s => s.Rows.Count);
        var where = string.Join(" and ", away.Select(s => s.Kind.DisplayName().ToLowerInvariant()).Distinct());
        Ui.Gap(0.6f);
        var missing = Pilot?.MissingDependency();
        if (missing is null) Ui.Hint($"{total} more item{(total == 1 ? "" : "s")} in your {where}. Gleam goes there for you when you press Clean.");
        else
        {
            Ui.Hint($"{total} more item{(total == 1 ? "" : "s")} in your {where}. Gleam cannot travel there: {missing}.");
            ImGui.SameLine();
            if (Ui.LinkButton("What it needs")) Show(Ui.AppMode.Settings);
        }
    }

    private void DrawSection(PlanSection section, List<PlanRow> rows)
    {
        var key = $"{section.Kind}:{section.OwnerId}";
        if (!sectionOpen.TryGetValue(key, out var open)) open = true;

        ImGui.SetNextItemOpen(open, ImGuiCond.Always);
        var checkedHere = rows.Count(r => r.Checked);
        var x0 = ImGui.GetCursorPosX();
        bool expanded;
        float labelStart;
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(10 * Ui.Scale, 6 * Ui.Scale)))
        {
            labelStart = ImGui.GetTreeNodeToLabelSpacing();
            expanded = ImGui.CollapsingHeader($"{section.Title}###{key}", ImGuiTreeNodeFlags.None);
        }
        sectionOpen[key] = expanded;
        var pillDrop = 5 * Ui.Scale; // pills are shorter than the padded header; centre them on it

        ImGui.SameLine();
        ImGui.SetCursorPosX(x0 + labelStart + ImGui.CalcTextSize(section.Title, false, 0).X + 22 * Ui.Scale);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + pillDrop);
        Ui.Pill(checkedHere > 0 ? $"{checkedHere} / {rows.Count}" : $"{rows.Count}", checkedHere > 0 ? Ui.AccentSoft : Ui.Muted);

        // A closed container only gets a marker when the player has to go there themselves; with
        // hands-free on, the run does the walking and the marker would just be noise.
        var handsFree = Pilot is not null && config.Automation.Enabled;
        if (!section.IsAvailableNow && !handsFree)
        {
            var why = section.Requirement;
            if (section.Kind == ContainerKind.GlamourDresser) why += $" · {section.FreeSlotsNeeded} free bag slots";
            ImGui.SameLine();
            Ui.RightAlign(30 * Ui.Scale);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + pillDrop + 2 * Ui.Scale);
            Ui.Icon(FontAwesomeIcon.MapMarkerAlt, Ui.Muted);
            Ui.Tooltip($"Not open right now: {why}. Clean anyway and these rows wait until it is.");
        }
        if (!expanded) { Ui.Gap(0.2f); return; }

        var cols = new List<HeaderColumn>();
        Vector2 tableMin, tableMax;
        using (var table = ImRaii.Table($"##t{key}", Simple ? 5 : 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.PadOuterX))
        {
        if (!table) return;
        ImGui.TableSetupColumn("##chk", ImGuiTableColumnFlags.WidthFixed, 24 * Ui.Scale, 0);
        ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, 30 * Ui.Scale, 0);
        ImGui.TableSetupColumn("##item", ImGuiTableColumnFlags.WidthStretch, 5f, 0);
        ImGui.TableSetupColumn("##action", Simple ? ImGuiTableColumnFlags.WidthStretch : ImGuiTableColumnFlags.WidthFixed, Simple ? 2f : 128 * Ui.Scale, 0);
        ImGui.TableSetupColumn("##market", ImGuiTableColumnFlags.WidthFixed, 96 * Ui.Scale, 0);
        if (!Simple) ImGui.TableSetupColumn("##attrs", ImGuiTableColumnFlags.WidthStretch, 5f, 0);

        // Column titles; the sortable ones cycle asc / desc / off for this section.
        ImGui.TableNextRow(ImGuiTableRowFlags.None, 24 * Ui.Scale);
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
        var titles = Simple
            ? new (string, SortKey?, bool)[] { ("Item", SortKey.Name, false), ("What happens", SortKey.Action, false), ("Market", SortKey.Market, true) }
            : new (string, SortKey?, bool)[] { ("Item", SortKey.Name, false), ("Action", SortKey.Action, false), ("Market", SortKey.Market, true), ("Attributes", null, false) };
        foreach (var (label, sortBy, numeric) in titles)
        {
            ImGui.TableNextColumn();
            var col = new HeaderColumn(ImGui.GetCursorScreenPos().X, ImGui.GetContentRegionAvail().X, label, sortBy, numeric);
            cols.Add(col);
            DrawHeaderTitle(key, col, string.Empty);
        }

        foreach (var row in rows)
        {
            var index = visibleRows.Count;
            visibleRows.Add(row);
            DrawRow(row, index);
        }
        }
        tableMin = ImGui.GetItemRectMin();
        tableMax = ImGui.GetItemRectMax();
        DrawStickyHeader(key, cols, tableMin, tableMax);
        Ui.Gap(0.5f);
    }

    private void DrawRow(PlanRow row, int index)
    {
        using var id = ImRaii.PushId(row.Key);
        using var fade = ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * RowAlpha(index));
        ImGui.TableNextRow(ImGuiTableRowFlags.None, 30 * Ui.Scale);
        // A just-toggled row glows gold for a moment; the keyboard cursor row is lifted.
        var glow = rowFlash.TryGetValue(row.Key, out var at) ? (float)Math.Clamp(1 - (ImGui.GetTime() - at) / 0.7, 0, 1) : 0f;
        if (glow > 0f) ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(Ui.Accent * new Vector4(1, 1, 1, 0.22f * glow * glow)));
        else if (index == cursor) ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(ImGuiCol.HeaderHovered));

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        var chk = row.Checked;
        if (Ui.Check("##c", ref chk, !row.IsExecutable)) SetChecked(row, chk);

        ImGui.TableNextColumn();
        Ui.ImageRounded(icons.Get(row.Info.IconId, row.Item.IsHq), new Vector2(26 * Ui.Scale, 26 * Ui.Scale), 4 * Ui.Scale);

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        var name = row.Info.Name + (row.Item.IsHq ? " " : "");
        // Clicking the name ticks the row, so the whole line is a target, not just the small box.
        if (ImGui.Selectable(name, index == cursor, ImGuiSelectableFlags.AllowItemOverlap, Vector2.Zero) && row.IsExecutable)
        {
            SetChecked(row, !row.Checked);
            cursor = index;
        }
        if (ImGui.IsItemHovered()) DrawRowTooltip(row);
        DrawRowContextMenu(row);
        if (row.Item.Quantity > 1) { ImGui.SameLine(); Ui.Hint($"× {row.Item.Quantity}"); }

        ImGui.TableNextColumn();
        if (Simple)
        {
            // The preset decides; the row only says what will happen. Advanced mode offers the alternatives.
            ImGui.AlignTextToFramePadding();
            Ui.TextColored(Ui.ActionColor(row.ChosenAction), row.ChosenAction.Label());
        }
        else DrawActionPicker(row);

        ImGui.TableNextColumn();
        DrawMarketPrice(row);

        if (Simple) return;
        ImGui.TableNextColumn();
        DrawAttributePills(row);
    }

    /// <summary>Lowest market-board listing on the home world, per unit. Blank for unmarketable items.</summary>
    private void DrawMarketPrice(PlanRow row)
    {
        ImGui.AlignTextToFramePadding();
        if (!row.Info.IsMarketable) return;
        var unit = row.Proposal.MarketUnitPrice;
        if (unit <= 0)
        {
            Ui.TextColored(Ui.Muted * new Vector4(1, 1, 1, 0.5f), config.UseUniversalis ? "no listings" : "prices off");
            Ui.Tooltip(config.UseUniversalis ? "Nobody is selling this on your home world right now." : "Market board prices are off.");
            return;
        }
        Ui.TextColored(Ui.Market, Simple ? $"{unit * row.Item.Quantity:N0}g" : $"{unit:N0}g");
        var scope = string.IsNullOrEmpty(coordinator.MarketScope) ? "your home world" : coordinator.MarketScope;
        Ui.Tooltip($"Lowest listing on {scope} ({(row.Item.IsHq ? "HQ" : "NQ")}): {unit:N0}g each · {unit * row.Item.Quantity:N0}g for the stack of {row.Item.Quantity}.");
    }

    private List<(string Text, Vector4 Color)> AttributePills(PlanRow row, bool includeType)
    {
        var info = row.Info;
        var item = row.Item;
        var pills = new List<(string Text, Vector4 Color)>();
        if (includeType) pills.Add((ItemTags.Of(info).Label(), Ui.AccentSoft));
        // One trade pill: the board implies tradeable, so say the strongest thing that is true.
        if (info.IsUntradable) pills.Add(("Untradeable", Ui.Warn));
        else if (info.IsMarketable) pills.Add(("Marketable", Ui.Ok));
        else pills.Add(("Tradeable", Ui.Muted));
        if (info.IsUnique) pills.Add(("Unique", Ui.Danger));
        if (info.IsIndisposable) pills.Add(("Cannot be discarded", Ui.Danger));
        if (row.Proposal.Registered is { } registered) pills.Add(registered ? ("Registered", Ui.Ok) : ("Not registered", Ui.Warn));
        if (info.IsUsable && row.Proposal.Registered is null) pills.Add(("Usable", Ui.Info));
        if (item.IsHq) pills.Add(("HQ", Ui.AccentSoft));
        if (item.HasMateria) pills.Add(($"{item.MateriaCount} materia", Ui.Accent));
        if (item.IsDyed) pills.Add((db.StainName(item.Stain0), Ui.Muted));
        return pills;
    }

    /// <summary>What the item *is*, as small pills. The why (rule, warnings) lives in the name tooltip.</summary>
    private void DrawAttributePills(PlanRow row)
    {
        var pills = AttributePills(row, includeType: false);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 3 * Ui.Scale);
        using var sp = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(4 * Ui.Scale, 0));
        for (var i = 0; i < pills.Count; i++)
        {
            Ui.Pill(pills[i].Text, pills[i].Color);
            if (i < pills.Count - 1) ImGui.SameLine();
        }
    }

    private void DrawActionPicker(PlanRow row)
    {
        var options = new List<ActionKind> { row.Proposal.Action };
        options.AddRange(row.Proposal.Alternatives.Where(a => a != row.Proposal.Action));
        if (!options.Contains(row.ChosenAction)) options.Insert(0, row.ChosenAction);
        // Just the verb; prices live in the market column and the tooltip.
        var labels = options.Select(a => a.Label()).ToList();
        var idx = options.IndexOf(row.ChosenAction);
        if (options.Count == 1)
        {
            // Nothing to choose: plain coloured text, aligned with the dropdowns on other rows.
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetStyle().FramePadding.X);
            ImGui.AlignTextToFramePadding();
            Ui.TextColored(Ui.ActionColor(row.ChosenAction), labels[0]);
            return;
        }
        var widest = labels.Max(l => ImGui.CalcTextSize(l, false, 0).X);
        ImGui.SetNextItemWidth(Math.Min(ImGui.GetContentRegionAvail().X, widest + ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 2));
        using var bg = ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Zero);
        var preview = ImRaii.PushColor(ImGuiCol.Text, Ui.ActionColor(row.ChosenAction));
        using (var combo = ImRaii.Combo("##act", labels[idx]))
        {
            preview.Dispose();
            if (!combo) return;
            // Each choice in its own action colour, so "discard" reads as red even under a green "sell".
            for (var i = 0; i < options.Count; i++)
            {
                using var col = ImRaii.PushColor(ImGuiCol.Text, Ui.ActionColor(options[i]));
                if (ImGui.Selectable(labels[i], i == idx, ImGuiSelectableFlags.None, Vector2.Zero))
                {
                    row.ChosenAction = options[i];
                    if (!row.IsExecutable) row.Checked = false;
                }
            }
        }
    }

    private void DrawRowTooltip(PlanRow row)
    {
        using var tip = Ui.RichTooltip();
        var info = row.Info;
        var item = row.Item;

        // Identity: icon, name, category line.
        var iconSize = 40 * Ui.Scale;
        Ui.ImageRounded(icons.Get(info.IconId, item.IsHq), new Vector2(iconSize, iconSize), 6 * Ui.Scale);
        ImGui.SameLine(0, 10 * Ui.Scale);
        using (ImRaii.Group())
        {
            Ui.TextColored(Ui.AccentSoft, info.Name + (item.IsHq ? " (HQ)" : string.Empty));
            var sub = info.UiCategory;
            if (info.IsEquipment) sub += $" · Item level {info.ItemLevel} · Level {info.LevelEquip}";
            sub += item.Quantity == 1 ? " · 1 in the stack" : $" · {item.Quantity} in the stack";
            Ui.Hint(sub);
        }

        Ui.Gap(0.4f);
        Ui.Rule();
        Ui.Gap(0.4f);
        var tags = AttributePills(row, includeType: true);
        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(4 * Ui.Scale, 4 * Ui.Scale)))
        {
            var x0 = ImGui.GetCursorPosX();
            var limit = ImGui.GetWindowContentRegionMax().X;
            for (var i = 0; i < tags.Count; i++)
            {
                var w = ImGui.CalcTextSize(tags[i].Text, false, 0).X + 16 * Ui.Scale;
                if (i > 0 && ImGui.GetCursorPosX() + w > limit) { ImGui.NewLine(); ImGui.SetCursorPosX(x0); }
                Ui.Pill(tags[i].Text, tags[i].Color);
                if (i < tags.Count - 1) ImGui.SameLine();
            }
        }

        var notes = row.Proposal.Warnings.Where(w => w != "Not proposed by any rule").ToList();
        if (notes.Count > 0)
        {
            Ui.Gap(0.4f);
            Ui.Rule();
            Ui.Gap(0.4f);
            foreach (var w in notes)
            {
                var severe = w.Contains("never be reacquired", StringComparison.Ordinal) || w.Contains("cannot be bought back", StringComparison.Ordinal);
                Ui.Icon(FontAwesomeIcon.ExclamationTriangle, severe ? Ui.Danger : Ui.Warn);
                ImGui.SameLine(0, 6 * Ui.Scale);
                using (ImRaii.TextWrapPos(0))
                    Ui.TextColored(severe ? Ui.Danger : Ui.Warn, w);
            }
        }

        Ui.Gap(0.4f);
        Ui.Rule();
        Ui.Gap(0.4f);

        // Worth and whereabouts.
        Ui.KeyValue("Vendor", info.VendorPrice > 0 ? $"{info.VendorPrice:N0}g each, {(long)info.VendorPrice * item.Quantity:N0}g for the stack" : "No vendor value");
        if (info.IsMarketable)
        {
            var mkt = row.Proposal.MarketUnitPrice;
            var scope = string.IsNullOrEmpty(coordinator.MarketScope) ? "home world" : coordinator.MarketScope;
            Ui.KeyValue("Market", mkt > 0 ? $"{mkt:N0}g each on {scope}, {mkt * item.Quantity:N0}g for the stack" : "No current listings", mkt > 0 ? Ui.Market : null);
        }
        Ui.KeyValue("Where", string.IsNullOrEmpty(item.OwnerName) ? item.Slot.Kind.DisplayName() : $"{item.Slot.Kind.DisplayName()} · {item.OwnerName}");

        Ui.Gap(0.5f);
        Ui.Hint("Right-click for options");
    }

    private void DrawRowContextMenu(PlanRow row)
    {
        using var popup = ImRaii.ContextPopupItem("##ctx");
        if (!popup) return;
        Ui.TextColored(Ui.Accent, row.Info.Name);
        ImGui.Separator();
        if (ImGui.MenuItem("Skip this time", string.Empty, false, true)) coordinator.SkipRow(row);
        if (ImGui.MenuItem("Keep this, always", string.Empty, false, true)) coordinator.Protect(row.Info.ItemId, row.Info.Name);
        if (ImGui.MenuItem("Treat as junk, always", string.Empty, false, true)) coordinator.AlwaysDiscard(row.Info.ItemId, row.Info.Name);
        ImGui.Separator();
        if (ImGui.MenuItem("Garland Tools", string.Empty, false, true)) Ui.OpenLink(Ui.GarlandUrl(row.Info.ItemId));
        if (ImGui.MenuItem("Market history (Universalis)", string.Empty, false, true)) Ui.OpenLink(Ui.UniversalisUrl(row.Info.ItemId));
        if (ImGui.MenuItem("Wiki", string.Empty, false, true)) Ui.OpenLink(Ui.WikiUrl(row.Info.Name));
    }

    private void DrawAlts(RunPlan plan)
    {
        Ui.Section("Other characters");
        foreach (var alt in plan.Alts)
        {
            var key = $"alt:{alt.CharacterId}";
            if (!sectionOpen.TryGetValue(key, out var open)) open = false;
            ImGui.SetNextItemOpen(open, ImGuiCond.Always);
            var expanded = ImGui.CollapsingHeader($"{alt.CharacterName}  ·  {alt.Proposals.Count}###{key}", ImGuiTreeNodeFlags.None);
            sectionOpen[key] = expanded;
            ImGui.SameLine();
            Ui.RightAlign(90 * Ui.Scale);
            Ui.Pill("View only", Ui.Muted);
            if (!expanded) continue;
            using var table = ImRaii.Table($"##alt{alt.CharacterId}", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.PadOuterX);
            if (!table) continue;
            ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, 30 * Ui.Scale, 0);
            ImGui.TableSetupColumn("##item", ImGuiTableColumnFlags.WidthStretch, 5f, 0);
            ImGui.TableSetupColumn("##why", ImGuiTableColumnFlags.WidthStretch, 5f, 0);
            foreach (var p in alt.Proposals.OrderBy(p => p.Info.Name))
            {
                ImGui.TableNextRow(ImGuiTableRowFlags.None, 30 * Ui.Scale);
                ImGui.TableNextColumn();
                var tex = icons.Get(p.Info.IconId, p.Item.IsHq);
                if (!tex.IsNull) ImGui.Image(tex, new Vector2(26 * Ui.Scale, 26 * Ui.Scale));
                ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); Ui.Text(p.Info.Name); if (p.Item.Quantity > 1) { ImGui.SameLine(); Ui.Hint($"× {p.Item.Quantity}"); }
                ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); Ui.Hint($"{p.Item.Slot.Kind.DisplayName()} · {p.Reason}");
            }
        }
    }

    private static void DrawExcludedNote(RunPlan plan)
    {
        if (plan.Excluded.Count == 0) return;
        Ui.Gap();
        var hard = plan.Excluded.Count(e => e.IsHardBlock);
        var prot = plan.Excluded.Count - hard;
        var parts = new List<string>();
        if (hard > 0) parts.Add($"{hard} that can never be touched");
        if (prot > 0) parts.Add($"{prot} on your never-touch list");
        Ui.Hint($"Not listed: {string.Join(", ", parts)}. Hover for why.");
        if (ImGui.IsItemHovered())
        {
            using var t = ImRaii.Tooltip();
            foreach (var g in plan.Excluded.GroupBy(e => e.Reason).OrderByDescending(g => g.Count()).Take(12))
                Ui.Text($"{g.Count()} × {g.Key}");
        }
    }

    // ---------- footer ----------

    private void DrawFooter(RunPlan plan)
    {
        Ui.Rule();
        Ui.Gap(0.3f);
        var summary = plan.Summarize();
        var t = coordinator.EffectiveProfile.Thresholds;
        var rowsForCap = coordinator.FocusContainer is { } f ? plan.Sections.Where(s => s.Kind == f).SelectMany(s => s.Rows) : plan.AllRows;
        var cap = SoftCap.Evaluate(rowsForCap, t);

        var freed = summary.SlotsFreedByContainer.Values.Sum();
        var parts = new List<string>();
        if (freed > 0) parts.Add($"frees {freed} slot{(freed == 1 ? "" : "s")}");
        if (summary.GilRecovered > 0) parts.Add($"recovers {Ui.Gil(summary.GilRecovered)}");
        if (summary.GilDestroyed > 0) parts.Add($"destroys {Ui.Gil(summary.GilDestroyed)} of vendor value");
        if (summary.MarketRows > 0) parts.Add($"lists {summary.MarketRows} on the market for about {Ui.Gil(summary.MarketGil)}");
        if (summary.SealsRows > 0) parts.Add($"{summary.SealsRows} to seals");
        if (coordinator.PendingActions.Count > 0) parts.Add($"{coordinator.PendingActions.Count} from earlier still waiting");

        ImGui.AlignTextToFramePadding();
        Ui.Hint(parts.Count == 0 ? "Tick rows to see what this run would do." : string.Join("  ·  ", parts));

        var (handsFree, needsTravel) = RunShape(plan);
        var blocked = needsTravel ? Pilot?.MissingDependency() : null;
        if (blocked is not null)
        {
            Ui.Gap(0.2f);
            Ui.TextColored(Ui.Danger, $"Gleam cannot travel: {blocked}. Open Settings to see what it needs.");
        }
        var items = $"{cap.Items} item{(cap.Items == 1 ? "" : "s")}";
        var verb = cap.Exceeded && !capArmed
            ? $"Yes, clean all {items}"
            : handsFree && needsTravel && !Simple ? $"Clean {items} everywhere" : $"Clean {items}";
        if (cap.Exceeded && !capArmed && Simple)
        {
            // A big run says so in words, on screen, instead of relying on a tooltip nobody hovers.
            Ui.Gap(0.2f);
            Ui.TextColored(Ui.Warn, $"That is a lot at once. {cap.Items} items, worth about {Ui.Gil(cap.GilAtRisk)}. Press again if you are sure.");
        }
        var style = ImGui.GetStyle();
        // The button narrows before the window does, so a small window never pushes it off the edge.
        var buttonWidth = Math.Clamp((ImGui.GetWindowWidth() - style.WindowPadding.X * 2) * 0.42f, 150 * Ui.Scale, 240 * Ui.Scale);
        var sortW = Simple ? 0f : ImGui.CalcTextSize(SortAfterLabel, false, 0).X + ImGui.GetFrameHeight() + style.ItemInnerSpacing.X + style.ItemSpacing.X * 2;
        var hereW = handsFree && needsTravel && !Simple ? ImGui.CalcTextSize("Clean here only", false, 0).X + style.FramePadding.X * 2 + style.ItemSpacing.X : 0;
        Ui.RightAlignOrWrap(sortW + hereW + buttonWidth, 160 * Ui.Scale);
        if (!Simple)
        {
            var sortAfter = config.SortAfterRun;
            if (Ui.Check(SortAfterLabel, ref sortAfter)) { config.SortAfterRun = sortAfter; config.Save(PluginServices.PluginInterface); }
            Ui.Tooltip(SortAfterHint);
        }
        if (handsFree && needsTravel && !Simple)
        {
            ImGui.SameLine();
            using (ImRaii.Disabled(cap.Items == 0))
            {
                if (Ui.LinkButton("Clean here only")) { capArmed = false; _ = coordinator.AcceptAsync(); }
            }
            Ui.Tooltip("Cleans what is reachable right now. The rest waits until you open its container.");
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(cap.Items == 0 || blocked is not null))
        {
            if (Ui.PrimaryButton(verb, buttonWidth, danger: cap.Exceeded && !capArmed)) Accept(plan);
        }
        var discards = plan.AllRows.Count(r => r.Checked && r.IsExecutable && r.ChosenAction == ActionKind.Discard);
        var irreversible = discards > 0 ? $"{discards} will be discarded for good. Sold and listed items can be bought back." : string.Empty;
        if (cap.Exceeded && !capArmed) Ui.Tooltip($"{cap.Explanation} Click again to go ahead.");
        else if (handsFree && needsTravel) Ui.Tooltip(string.IsNullOrEmpty(irreversible) ? HandsFreeCleanHint : $"{HandsFreeCleanHint} {irreversible}");
        else Ui.Tooltip(irreversible);
    }

    public const string SortAfterLabel = "Sort bags afterwards";
    public const string SortAfterHint = "Runs the game's own sort on every container that was touched.";
    public const string HandsFreeCleanHint = "Hands-free: opens the saddlebag, travels to an inn, visits each retainer and the dresser, and cleans as it goes.";
    private const string StopHint = "Finishes the current item, then stops.";

    // ---------- first run: three screens, once ----------

    private int firstRunStep;

    private static readonly (bool Clean, bool Organize, string Title, string Text)[] FirstRunPurposes =
    [
        (true, false, "Clear out my junk", "Gleam finds what is not worth keeping and shows you the list. You decide what happens to it."),
        (false, true, "Put my things away", "Gleam moves what you keep to where you want it. Nothing is ever thrown away or sold."),
        (true, true, "Both", "Clear out the junk, and put away what is left."),
    ];

    private static readonly (Core.Rules.PresetName Preset, string Title, string Text)[] FirstRunPresets =
    [
        (Core.Rules.PresetName.Vendor, "Sell it to vendors", "The safe choice. Junk worth gil is sold, the rest is thrown away."),
        (Core.Rules.PresetName.MarketBoard, "Sell it on the market board", "Earns the most. Your retainers list what other players buy."),
        (Core.Rules.PresetName.DiscardAll, "Just throw it away", "Fastest. Nothing is sold and nothing comes back."),
    ];

    /// <summary>
    /// First run. One question, or two for anyone who wants junk cleared: what is Gleam for, and then what
    /// should happen to junk. Someone who only wants their things put away is never asked about discarding.
    /// </summary>
    private void DrawFirstRun(RunPlan plan)
    {
        var width = Math.Min(520 * Ui.Scale, ImGui.GetContentRegionAvail().X - 20 * Ui.Scale);
        var left = (ImGui.GetWindowWidth() - width) / 2;
        var junkStep = firstRunStep == 1;
        Ui.RunningHeader(icons.LogoMedium,
            junkStep ? "What should happen to the junk?" : "What would you like Gleam to do?",
            junkStep ? "Gleam always shows you the list first. Nothing happens until you press the button."
                     : "You can change this later, and turn the other half on whenever you like.");
        Ui.Gap(1f);

        if (!junkStep)
        {
            foreach (var (clean, organize, title, text) in FirstRunPurposes)
            {
                ImGui.SetCursorPosX(left);
                if (OptionCard(title, text, false, width))
                {
                    config.UseClean = clean;
                    config.UseOrganize = organize;
                    config.AnsweredOrganizeOffer = clean && organize;
                    if (clean) firstRunStep = 1;
                    else
                    {
                        // Organize only: no junk question at all, straight to where things go.
                        config.SeenFirstRun = true;
                        config.SeenOrganizeIntro = true;
                        config.Save(PluginServices.PluginInterface);
                        Show(Ui.AppMode.Organize);
                    }
                }
                Ui.Gap(0.35f);
            }
            _ = plan;
            return;
        }

        foreach (var (preset, title, text) in FirstRunPresets)
        {
            ImGui.SetCursorPosX(left);
            if (OptionCard(title, text, false, width))
            {
                config.Profiles.Account.ApplyPreset(preset);
                config.SeenFirstRun = true;
                config.SeenCleanIntro = true;
                config.Save(PluginServices.PluginInterface);
                _ = coordinator.RefreshPlanAsync(openWindow: false);
            }
            Ui.Gap(0.35f);
        }
        Ui.Gap(0.4f);
        ImGui.SetCursorPosX(left);
        if (Ui.LinkButton("◂  Back")) firstRunStep = 0;
        _ = plan;
    }

    /// <summary>A wide choice card: title, one line, a tick when it is the current choice. Returns true when clicked.</summary>
    private static bool OptionCard(string title, string text, bool selected, float width)
    {
        var h = ImGui.GetTextLineHeight() * 2 + 26 * Ui.Scale;
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton($"##opt{title}", new Vector2(width, h));
        var key = $"opt:{title}";
        Ui.RecordHover(key);
        var hv = Ui.Hover(key);
        var on = Ui.Smooth(key + ":on", selected ? 1f : 0f, 14f);
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + new Vector2(width, h), ImGui.GetColorU32(Ui.Mix(new Vector4(1, 1, 1, 0.04f + 0.04f * hv), Ui.Accent * new Vector4(1, 1, 1, 0.18f), on)), Ui.Rounding);
        dl.AddRect(pos, pos + new Vector2(width, h), ImGui.GetColorU32(Ui.Mix(Ui.InkLine, Ui.Accent, on)), Ui.Rounding);
        var pad = 14 * Ui.Scale;
        dl.AddText(pos + new Vector2(pad, 10 * Ui.Scale), ImGui.GetColorU32(Ui.Mix(Ui.Cream, Ui.AccentSoft, on)), title);
        dl.AddText(pos + new Vector2(pad, 14 * Ui.Scale + ImGui.GetTextLineHeight()), ImGui.GetColorU32(Ui.Muted), text);
        if (on > 0.01f)
        {
            using var f = ImRaii.PushFont(UiBuilder.IconFont);
            dl.AddText(pos + new Vector2(width - pad - ImGui.GetTextLineHeight(), (h - ImGui.GetTextLineHeight()) / 2), ImGui.GetColorU32(Ui.AccentSoft * new Vector4(1, 1, 1, on)), FontAwesomeIcon.Check.ToIconString());
        }
        return clicked;
    }

    /// <summary>Whether this run would go hands-free, and whether anything ticked needs travel to reach.</summary>
    private (bool HandsFree, bool NeedsTravel) RunShape(RunPlan plan)
    {
        var handsFree = Pilot is not null && config.Automation.Enabled && coordinator.FocusContainer is null && !Pilot.IsRunning;
        var needsTravel = plan.AllRows.Any(r => r.Checked && r.IsExecutable && (!r.Item.Slot.Kind.IsAlwaysLoaded() || r.ChosenAction is ActionKind.VendorSell or ActionKind.MarketList));
        return (handsFree, needsTravel);
    }

    /// <summary>The one way a run starts, whether from the button, Enter or the gamepad: honours the cap and hands-free.</summary>
    private void Accept(RunPlan plan)
    {
        if (coordinator.IsRunning) return;
        var rowsForCap = coordinator.FocusContainer is { } f ? plan.Sections.Where(s => s.Kind == f).SelectMany(s => s.Rows) : plan.AllRows;
        var cap = SoftCap.Evaluate(rowsForCap, coordinator.EffectiveProfile.Thresholds);
        if (cap.Items == 0) return;
        if (cap.Exceeded && !capArmed) { capArmed = true; return; }
        capArmed = false;
        var (handsFree, needsTravel) = RunShape(plan);
        if (handsFree && needsTravel) _ = Pilot!.RunAsync(); else _ = coordinator.AcceptAsync();
    }

    private string FocusName(RunPlan plan, ContainerKind kind)
    {
        if (kind == ContainerKind.Retainer)
        {
            var names = plan.Sections.Where(s => s.Kind == kind).Select(s => s.OwnerName).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
            if (names.Count == 1) return $"retainer {names[0]}";
        }
        return kind.DisplayName().ToLowerInvariant();
    }

    private void DrawPilotRunning()
    {
        // The whole trip is the bar; the step under way is the line beneath the title.
        var pilot = Pilot!;
        var current = coordinator.LastProgress is { } p && coordinator.IsRunning ? $"{p.Action.ItemName}{(p.Action.Quantity > 1 ? $" × {p.Action.Quantity}" : "")}" : null;
        Ui.RunningHeader(icons.LogoMedium, "Cleaning hands-free", pilot.Status);
        Ui.Gap(0.8f);
        var width = Math.Clamp(ImGui.GetWindowWidth() * 0.6f, 260 * Ui.Scale, 720 * Ui.Scale);
        var left = (ImGui.GetWindowWidth() - width) / 2;
        var total = pilot.PlannedTotal;
        ImGui.SetCursorPosX(left);
        Ui.ProgressBar("pilot", total > 0 ? (float)pilot.PlannedDone / total : null, width, Ui.ProgressLabel(pilot.PlannedDone, total), "Whole run");
        Ui.Gap(0.5f);
        ImGui.SetCursorPosX(left);
        var stopTotal = coordinator.IsRunning ? coordinator.RunTotal : 0;
        Ui.ProgressBar("pilot-stop", stopTotal > 0 ? (float)coordinator.RunDone / stopTotal : null, width,
            stopTotal > 0 ? Ui.ProgressLabel(coordinator.RunDone, stopTotal) : null, coordinator.IsRunning ? "At this stop" : "On the way", primary: false);
        if (current is not null) { Ui.Gap(0.3f); ImGui.SetCursorPosX(left); Ui.Hint(current); }
        Ui.Gap(1.2f);
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - 120 * Ui.Scale) / 2);
        if (Ui.PrimaryButton("Stop", 120 * Ui.Scale, danger: true)) pilot.Stop();
        Ui.Gap(0.3f);
        DrawCentered(StopHint, muted: true);
    }

    private void DrawRunning()
    {
        var p = coordinator.LastProgress;
        Ui.RunningHeader(icons.LogoMedium, "Cleaning", p is null ? null : $"{p.Action.ItemName}{(p.Action.Quantity > 1 ? $" × {p.Action.Quantity}" : "")} · {p.Message}");
        Ui.Gap(0.8f);
        var width = Math.Clamp(ImGui.GetWindowWidth() * 0.6f, 260 * Ui.Scale, 720 * Ui.Scale);
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - width) / 2);
        var total = coordinator.RunTotal;
        Ui.ProgressBar("clean", total > 0 ? (float)coordinator.RunDone / total : null, width, Ui.ProgressLabel(coordinator.RunDone, total));
        Ui.Gap(1.2f);
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - 120 * Ui.Scale) / 2);
        if (Ui.PrimaryButton("Stop", 120 * Ui.Scale, danger: true)) coordinator.CancelRun();
        Ui.Gap(0.3f);
        DrawCentered(StopHint, muted: true);
    }

    private static void DrawCentered(string text, bool muted = false) => Ui.Centered(text, muted);

    // ---------- keyboard & gamepad ----------

    private void HandleKeyboard(RunPlan plan)
    {
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) || ImGui.IsAnyItemActive()) return;
        var count = visibleRows.Count;
        if (count == 0) { cursor = -1; return; }

        var down = ImGui.IsKeyPressed(ImGuiKey.DownArrow, true);
        var up = ImGui.IsKeyPressed(ImGuiKey.UpArrow, true);
        var toggle = ImGui.IsKeyPressed(ImGuiKey.Space, false);
        var accept = ImGui.IsKeyPressed(ImGuiKey.Enter, false);
        var cancel = ImGui.IsKeyPressed(ImGuiKey.Escape, false);

        down |= gamepad.Pressed(GamepadButtons.DpadDown) > 0;
        up |= gamepad.Pressed(GamepadButtons.DpadUp) > 0;
        toggle |= gamepad.Pressed(GamepadButtons.South) > 0;
        accept |= gamepad.Pressed(GamepadButtons.West) > 0;
        cancel |= gamepad.Pressed(GamepadButtons.East) > 0;

        if (down) cursor = Math.Min(count - 1, cursor + 1);
        if (up) cursor = Math.Max(0, cursor - 1);
        if (toggle && cursor >= 0 && cursor < count)
        {
            var row = visibleRows[cursor];
            if (row.IsExecutable) SetChecked(row, !row.Checked);
        }
        if (accept) Accept(plan);
        if (cancel) IsOpen = false;
    }
}
