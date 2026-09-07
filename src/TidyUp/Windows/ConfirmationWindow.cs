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

    private string search = string.Empty;
    private ContainerKind? filterContainer;
    private string? filterRule;
    private ActionKind? filterAction;
    private readonly HashSet<ItemTag> filterTags = new();
    /// <summary>null = both, true = tradeable only, false = untradeable only.</summary>
    private bool? filterTradeable;
    private int sortMode; // 0 name, 1 value, 2 quantity
    private bool capArmed;
    private int cursor = -1;
    private readonly List<PlanRow> visibleRows = new();
    private readonly Dictionary<string, bool> sectionOpen = new();

    public ConfirmationWindow(RunCoordinator coordinator, IconCache icons, ItemDatabase db, Configuration config, IGamepadState gamepad, Action openSettings, Action openHistory)
        : base("Tidy Up###TidyUpConfirm")
    {
        this.coordinator = coordinator;
        this.icons = icons;
        this.db = db;
        this.config = config;
        this.gamepad = gamepad;
        Size = new Vector2(860, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(560, 320), MaximumSize = new Vector2(4000, 3000) };
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = Dalamud.Interface.FontAwesomeIcon.History,
            Click = _ => openHistory(),
            ShowTooltip = () => ImGui.SetTooltip("History"),
        });
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = Dalamud.Interface.FontAwesomeIcon.Cog,
            Click = _ => openSettings(),
            ShowTooltip = () => ImGui.SetTooltip("Settings"),
        });
    }

    public override void OnOpen()
    {
        capArmed = false;
        cursor = -1;
        if (coordinator.CurrentPlan is null && !coordinator.IsRunning)
            _ = coordinator.RefreshPlanAsync(openWindow: false);
    }

    public override void Draw()
    {
        var plan = coordinator.CurrentPlan;
        if (Pilot is { IsRunning: true } && !Pilot.Status.StartsWith("Waiting for you", StringComparison.Ordinal)) { DrawPilotRunning(); return; }
        if (coordinator.IsRunning) { DrawRunning(); return; }
        if (plan is null) { DrawCentered(string.IsNullOrEmpty(coordinator.Status) ? "Scanning your containers…" : coordinator.Status); return; }
        if (Pilot is { IsRunning: true }) { Ui.TextColored(Ui.Accent, Pilot.Status); Ui.Hint("Clean what you agree with, or close this window to skip it. The run continues either way."); Ui.Gap(0.5f); }

        DrawTopBar(plan);
        DrawLastRunBanner();
        Ui.Gap(0.5f);

        var footer = ImGui.GetFrameHeight() * 2.4f + Ui.Space;
        using (var child = ImRaii.Child("##rows", new Vector2(0, -footer), false, ImGuiWindowFlags.None))
        {
            if (child)
            {
                visibleRows.Clear();
                var sections = coordinator.FocusContainer is { } f ? plan.Sections.Where(s => s.Kind == f) : plan.Sections;
                var drewAny = false;
                foreach (var section in sections.OrderBy(s => s.Kind.ExecutionOrder()).ThenBy(s => s.OwnerName))
                {
                    var rows = Filter(section.Rows).ToList();
                    if (rows.Count == 0) continue;
                    drewAny = true;
                    DrawSection(section, rows);
                }
                if (coordinator.FocusContainer is null && plan.Alts.Count > 0) DrawAlts(plan);
                if (!drewAny)
                {
                    if (plan.AllRows.Any()) Ui.EmptyState(icons.Logo, "Nothing matches your filters.", "Clear a chip or the search box to see more.");
                    else Ui.EmptyState(icons.Logo, "Nothing to clean.", "Everything looks tidy.");
                }
                DrawExcludedNote(plan);
            }
        }

        HandleKeyboard();
        DrawFooter(plan);
    }

    private bool bannerDismissed;
    private Core.Execution.RunReport? bannerReport;

    /// <summary>One quiet line after a run: what happened and what is still waiting. Dismissed with a click.</summary>
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
            parts.Add($"{count} waiting to {(string.IsNullOrEmpty(reason) ? "have their container opened" : reason)}");
        if (parts.Count == 0) return;

        Ui.Gap(0.3f);
        var color = report.Failed > 0 ? Ui.Warn : Ui.Ok;
        if (Ui.Banner(color, "Last run", string.Join(" · ", parts))) bannerDismissed = true;
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
            ? $"{fc.DisplayName()} · {checkedCount} of {total} selected"
            : $"{total} item{(total == 1 ? "" : "s")} in {containers} container{(containers == 1 ? "" : "s")} · {checkedCount} selected";
        Ui.Header(icons.Logo, "Tidy Up", subtitle, Ui.SegmentedWidth(PresetOptions), () =>
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
            Ui.Tooltip("Sell on marketboard: lists marketable items through your retainers at the lowest price on your home world, vendors other tradeable items, discards untradeable ones.\nSell on vendors: vendors tradeable items, discards untradeable ones.\nDiscard all: discards everything proposed.");
        }, profile.Thresholds.Policy.Describe());

        Ui.Gap(0.4f);

        DrawContainerChips(plan);
        DrawTypeChips(plan);

        Ui.SearchBox("##search", ref search, 260 * Ui.Scale);

        ImGui.SameLine();
        var filtersActive = filterContainer is not null || filterRule is not null || filterAction is not null || filterTags.Count > 0 || filterTradeable is not null || sortMode != 0;
        if (Ui.IconButton(FontAwesomeIcon.Filter, filtersActive ? "Filter •" : "Filter")) ImGui.OpenPopup("##filters", ImGuiPopupFlags.None);
        DrawFilterMenu();

        ImGui.SameLine();
        if (Ui.IconButton(FontAwesomeIcon.Sync, "Rescan")) _ = coordinator.RefreshPlanAsync(openWindow: false, coordinator.FocusContainer);

        ImGui.SameLine();
        Ui.RightAlign(90 * Ui.Scale);
        // All means all of what is on screen: with a type, container or search filter on, it ticks just
        // those rows. The soft cap still asks for a second click on a big run.
        var narrowed = filterContainer is not null || filterRule is not null || filterAction is not null || filterTags.Count > 0 || filterTradeable is not null || !string.IsNullOrWhiteSpace(search);
        var executable = (narrowed ? Filter(plan.AllRows) : plan.AllRows).Where(r => r.IsExecutable).ToList();
        var allChecked = executable.Count > 0 && executable.All(r => r.Checked);
        if (Ui.LinkButton(allChecked ? "None" : narrowed ? "All shown" : "All"))
        {
            foreach (var r in executable)
            {
                r.Checked = !allChecked;
                if (allChecked) coordinator.SessionSkips.Add(r.Key); else coordinator.SessionSkips.Remove(r.Key);
            }
        }
        var warned = executable.Count(r => r.Proposal.Warnings.Count > 0);
        var scope = narrowed ? "every row that matches the current filters" : "every row";
        Ui.Tooltip(warned > 0
            ? $"Ticks {scope}, including {warned} with a warning (usable, untradeable, or valuable on the market). Glance at those before you clean."
            : $"Ticks {scope}.");
    }

    /// <summary>One chip per container with its row count. Click to show only that container; click again for all.</summary>
    private void DrawContainerChips(RunPlan plan)
    {
        if (coordinator.FocusContainer is not null) return;
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

        Ui.Hint("Container");
        if (ImGui.MenuItem("All containers", string.Empty, filterContainer is null, true)) filterContainer = null;
        foreach (var k in Enum.GetValues<ContainerKind>())
            if (ImGui.MenuItem(k.DisplayName(), string.Empty, filterContainer == k, true)) filterContainer = k;

        ImGui.Separator();
        Ui.Hint("Rule");
        if (ImGui.MenuItem("All rules", string.Empty, filterRule is null, true)) filterRule = null;
        foreach (var r in Core.Rules.RuleEngine.AllRules)
            if (ImGui.MenuItem(r.Name, string.Empty, filterRule == r.Id, true)) filterRule = r.Id;
        if (ImGui.MenuItem("Always-discard list", string.Empty, filterRule == "always-discard", true)) filterRule = "always-discard";
        if (ImGui.MenuItem("Not proposed", string.Empty, filterRule == "manual", true)) filterRule = "manual";

        ImGui.Separator();
        Ui.Hint("Type");
        if (ImGui.MenuItem("All types", string.Empty, filterTags.Count == 0, true)) filterTags.Clear();
        foreach (var t in Enum.GetValues<ItemTag>())
            if (ImGui.MenuItem(t.Label(), string.Empty, filterTags.Contains(t), true)) { if (!filterTags.Remove(t)) filterTags.Add(t); }

        ImGui.Separator();
        Ui.Hint("Trade");
        if (ImGui.MenuItem("Tradeable and untradeable", string.Empty, filterTradeable is null, true)) filterTradeable = null;
        if (ImGui.MenuItem("Tradeable only", string.Empty, filterTradeable == true, true)) filterTradeable = true;
        if (ImGui.MenuItem("Untradeable only", string.Empty, filterTradeable == false, true)) filterTradeable = false;

        ImGui.Separator();
        Ui.Hint("Action");
        if (ImGui.MenuItem("All actions", string.Empty, filterAction is null, true)) filterAction = null;
        foreach (var a in new[] { ActionKind.Discard, ActionKind.VendorSell, ActionKind.MarketList, ActionKind.ExpertDelivery, ActionKind.Desynth, ActionKind.None })
            if (ImGui.MenuItem(a.Label(), string.Empty, filterAction == a, true)) filterAction = a;

        ImGui.Separator();
        Ui.Hint("Sort");
        if (ImGui.MenuItem("Name", string.Empty, sortMode == 0, true)) sortMode = 0;
        if (ImGui.MenuItem("Value", string.Empty, sortMode == 1, true)) sortMode = 1;
        if (ImGui.MenuItem("Quantity", string.Empty, sortMode == 2, true)) sortMode = 2;

        ImGui.Separator();
        if (ImGui.MenuItem("Reset", string.Empty, false, true)) { filterContainer = null; filterRule = null; filterAction = null; filterTags.Clear(); filterTradeable = null; sortMode = 0; }
    }

    private IEnumerable<PlanRow> Filter(IEnumerable<PlanRow> rows)
    {
        var q = rows;
        if (filterContainer is { } c) q = q.Where(r => r.Item.Slot.Kind == c);
        if (filterRule is { } rule) q = q.Where(r => r.Proposal.RuleId == rule);
        if (filterAction is { } a) q = q.Where(r => r.ChosenAction == a);
        if (filterTags.Count > 0) q = q.Where(r => filterTags.Contains(ItemTags.Of(r.Info)));
        if (filterTradeable is { } tradeOnly) q = q.Where(r => r.Info.IsUntradable != tradeOnly);
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(r => r.Info.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || r.Proposal.Reason.Contains(search, StringComparison.OrdinalIgnoreCase));
        return sortMode switch
        {
            1 => q.OrderByDescending(r => r.Proposal.ValueGil).ThenBy(r => r.Info.Name),
            2 => q.OrderByDescending(r => r.Item.Quantity).ThenBy(r => r.Info.Name),
            _ => q.OrderBy(r => r.Info.Name, StringComparer.OrdinalIgnoreCase),
        };
    }

    // ---------- sections ----------

    private void DrawSection(PlanSection section, List<PlanRow> rows)
    {
        var key = $"{section.Kind}:{section.OwnerId}";
        var defaultOpen = !(section.Kind == ContainerKind.Retainer && coordinator.EffectiveProfile.RetainerSectionsCollapsed);
        if (!sectionOpen.TryGetValue(key, out var open)) open = defaultOpen;

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
            Ui.Tooltip($"Not open right now: {why}. Accept anyway and these rows wait until it is.");
        }
        if (!expanded) { Ui.Gap(0.2f); return; }

        using var table = ImRaii.Table($"##t{key}", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.PadOuterX);
        if (!table) return;
        ImGui.TableSetupColumn("##chk", ImGuiTableColumnFlags.WidthFixed, 24 * Ui.Scale, 0);
        ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, 30 * Ui.Scale, 0);
        ImGui.TableSetupColumn("##item", ImGuiTableColumnFlags.WidthStretch, 5f, 0);
        ImGui.TableSetupColumn("##action", ImGuiTableColumnFlags.WidthFixed, 128 * Ui.Scale, 0);
        ImGui.TableSetupColumn("##market", ImGuiTableColumnFlags.WidthFixed, 96 * Ui.Scale, 0);
        ImGui.TableSetupColumn("##attrs", ImGuiTableColumnFlags.WidthStretch, 5f, 0);

        foreach (var row in rows)
        {
            var index = visibleRows.Count;
            visibleRows.Add(row);
            DrawRow(row, index);
        }
        Ui.Gap(0.5f);
    }

    private void DrawRow(PlanRow row, int index)
    {
        using var id = ImRaii.PushId(row.Key);
        ImGui.TableNextRow(ImGuiTableRowFlags.None, 30 * Ui.Scale);
        if (index == cursor) ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(ImGuiCol.HeaderHovered));

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        var chk = row.Checked;
        using (ImRaii.Disabled(!row.IsExecutable))
        {
            if (ImGui.Checkbox("##c", ref chk))
            {
                row.Checked = chk;
                if (!chk) coordinator.SessionSkips.Add(row.Key); else coordinator.SessionSkips.Remove(row.Key);
            }
        }

        ImGui.TableNextColumn();
        Ui.ImageRounded(icons.Get(row.Info.IconId, row.Item.IsHq), new Vector2(26 * Ui.Scale, 26 * Ui.Scale), 4 * Ui.Scale);

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        var name = row.Info.Name + (row.Item.IsHq ? " " : "");
        ImGui.Selectable(name, index == cursor, ImGuiSelectableFlags.AllowItemOverlap, Vector2.Zero);
        if (ImGui.IsItemHovered()) DrawRowTooltip(row);
        DrawRowContextMenu(row);
        ImGui.SameLine();
        Ui.Hint($"× {row.Item.Quantity}");

        ImGui.TableNextColumn();
        DrawActionPicker(row);

        ImGui.TableNextColumn();
        DrawMarketPrice(row);

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
            Ui.Tooltip(config.UseUniversalis ? "Universalis has no current listing for this item on your home world." : "Turn on market prices in Settings › Advanced › Integrations.");
            return;
        }
        Ui.TextColored(Ui.Market, $"{unit:N0}g");
        var scope = string.IsNullOrEmpty(coordinator.MarketScope) ? "your home world" : coordinator.MarketScope;
        Ui.Tooltip($"Lowest listing on {scope} ({(row.Item.IsHq ? "HQ" : "NQ")}): {unit:N0}g each · {unit * row.Item.Quantity:N0}g for the stack of {row.Item.Quantity}.");
    }

    /// <summary>What the item *is*, as small pills. The why (rule, warnings) lives in the name tooltip.</summary>
    private void DrawAttributePills(PlanRow row)
    {
        var info = row.Info;
        var item = row.Item;
        var pills = new List<(string Text, Vector4 Color)>();
        if (info.IsUntradable) pills.Add(("untradeable", Ui.Warn)); else pills.Add(("tradeable", Ui.Muted));
        if (info.IsMarketable) pills.Add(("marketable", Ui.Ok));
        if (info.IsUnique) pills.Add(("unique", Ui.Danger));
        if (info.IsIndisposable) pills.Add(("cannot discard", Ui.Danger));
        if (info.IsUsable) pills.Add(("usable", Ui.Info));
        if (item.IsHq) pills.Add(("HQ", Ui.AccentSoft));
        if (item.HasMateria) pills.Add(($"{item.MateriaCount} materia", Ui.Accent));
        if (item.IsDyed) pills.Add((db.StainName(item.Stain0), Ui.Muted));

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
        var labels = options.Select(a => a == row.ChosenAction && row.Proposal.ValueLabel != "—" ? $"{a.Label()} · {row.Proposal.ValueLabel}" : a.Label()).ToList();
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
        using var t = ImRaii.Tooltip();
        Ui.TextColored(Ui.Accent, row.Info.Name);
        Ui.Hint($"{row.Info.UiCategory} · iL{row.Info.ItemLevel} · lv{row.Info.LevelEquip} · id {row.Info.ItemId}");
        Ui.Gap(0.3f);
        Ui.Text(row.Proposal.Reason);
        foreach (var w in row.Proposal.Warnings) Ui.TextColored(Ui.Warn, w);
        Ui.Gap(0.3f);
        Ui.Hint($"Vendor {Ui.Gil(row.Info.VendorPrice)} each · {(row.Info.IsMarketable ? "marketable" : "not marketable")}{(row.Info.IsUntradable ? " · untradeable" : "")}");
        Ui.Hint($"Slot {row.Item.Slot} · {row.Proposal.RuleId}");
        if (row.Proposal.Alternatives.Count > 0) Ui.Hint($"Also possible: {string.Join(", ", row.Proposal.Alternatives.Select(a => a.Label()))}");
        Ui.Gap(0.3f);
        Ui.Hint("Right-click for options");
    }

    private void DrawRowContextMenu(PlanRow row)
    {
        using var popup = ImRaii.ContextPopupItem("##ctx");
        if (!popup) return;
        Ui.TextColored(Ui.Accent, row.Info.Name);
        ImGui.Separator();
        if (ImGui.MenuItem("Skip this time", string.Empty, false, true)) coordinator.SkipRow(row);
        if (ImGui.MenuItem("Never discard", string.Empty, false, true)) coordinator.Protect(row.Info.ItemId, row.Info.Name);
        if (ImGui.MenuItem("Always discard", string.Empty, false, true)) coordinator.AlwaysDiscard(row.Info.ItemId, row.Info.Name);
        ImGui.Separator();
        if (ImGui.MenuItem("Garland Tools", string.Empty, false, true)) Ui.OpenLink(Ui.GarlandUrl(row.Info.ItemId));
        if (ImGui.MenuItem("Universalis", string.Empty, false, true)) Ui.OpenLink(Ui.UniversalisUrl(row.Info.ItemId));
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
            Ui.Pill("read-only", Ui.Muted);
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
                ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); Ui.Text(p.Info.Name); ImGui.SameLine(); Ui.Hint($"× {p.Item.Quantity}");
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
        if (hard > 0) parts.Add($"{hard} protected by hard rules");
        if (prot > 0) parts.Add($"{prot} on your protect list");
        Ui.Hint($"Not shown: {string.Join(", ", parts)}.");
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
        if (coordinator.PendingActions.Count > 0) parts.Add($"{coordinator.PendingActions.Count} accepted earlier still waiting");

        ImGui.AlignTextToFramePadding();
        Ui.Hint(parts.Count == 0 ? "Select rows to see what this run would do." : string.Join("  ·  ", parts));

        var handsFree = Pilot is not null && config.Automation.Enabled && coordinator.FocusContainer is null && !(Pilot?.IsRunning ?? false);
        var needsTravel = plan.AllRows.Any(r => r.Checked && r.IsExecutable && (!r.Item.Slot.Kind.IsAlwaysLoaded() || r.ChosenAction is ActionKind.VendorSell or ActionKind.MarketList));
        var verb = cap.Exceeded && !capArmed
            ? $"Clean {cap.Items} · over your cap, click again"
            : handsFree && needsTravel ? $"Clean {cap.Items} everywhere" : $"Clean {cap.Items} item{(cap.Items == 1 ? "" : "s")}";
        var buttonWidth = 240 * Ui.Scale;
        ImGui.SameLine();
        Ui.RightAlign(buttonWidth + (handsFree && needsTravel ? 170 : 70) * Ui.Scale);
        if (Ui.LinkButton("Close")) IsOpen = false;
        if (handsFree && needsTravel)
        {
            ImGui.SameLine();
            using (ImRaii.Disabled(cap.Items == 0))
            {
                if (Ui.LinkButton("Clean here only")) { capArmed = false; _ = coordinator.AcceptAsync(); }
            }
            Ui.Tooltip("Cleans what is reachable right now and leaves the rest waiting for their container.");
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(cap.Items == 0))
        {
            if (Ui.PrimaryButton(verb, buttonWidth, danger: cap.Exceeded && !capArmed))
            {
                if (cap.Exceeded && !capArmed) capArmed = true;
                else
                {
                    capArmed = false;
                    if (handsFree && needsTravel) _ = Pilot!.RunAsync(); else _ = coordinator.AcceptAsync();
                }
            }
        }
        if (cap.Exceeded) Ui.Tooltip(cap.Explanation);
        else if (handsFree && needsTravel) Ui.Tooltip("Hands-free: opens the saddlebag, travels to an inn, visits each retainer and the dresser, and cleans as it goes.");
    }

    private void DrawPilotRunning()
    {
        Ui.EmptyState(icons.Logo, "Hands-free run", Pilot!.Status);
        Ui.Gap(0.5f);
        if (coordinator.IsRunning)
        {
            var frac = coordinator.RunTotal == 0 ? 0f : (float)coordinator.RunDone / coordinator.RunTotal;
            ImGui.SetCursorPosX(ImGui.GetWindowWidth() * 0.2f);
            Ui.Progress(frac, ImGui.GetWindowWidth() * 0.6f, $"{coordinator.RunDone} / {coordinator.RunTotal}");
        }
        Ui.Gap();
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - 120 * Ui.Scale) / 2);
        if (Ui.PrimaryButton("Stop", 120 * Ui.Scale, danger: true)) Pilot.Stop();
        DrawCentered("Stops moving and finishes only the current item.", muted: true);
    }

    private void DrawRunning()
    {
        Ui.EmptyState(icons.Logo, "Cleaning…");
        Ui.Gap(0.5f);
        var frac = coordinator.RunTotal == 0 ? 0f : (float)coordinator.RunDone / coordinator.RunTotal;
        ImGui.SetCursorPosX(ImGui.GetWindowWidth() * 0.2f);
        Ui.Progress(frac, ImGui.GetWindowWidth() * 0.6f, $"{coordinator.RunDone} / {coordinator.RunTotal}");
        if (coordinator.LastProgress is { } p) DrawCentered($"{p.Action.ItemName} × {p.Action.Quantity} · {p.Message}", muted: true);
        Ui.Gap();
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - 120 * Ui.Scale) / 2);
        if (Ui.PrimaryButton("Stop", 120 * Ui.Scale, danger: true)) coordinator.CancelRun();
        DrawCentered("Stopping finishes the current item and leaves the rest untouched.", muted: true);
    }

    private static void DrawCentered(string text, bool muted = false) => Ui.Centered(text, muted);

    // ---------- keyboard & gamepad ----------

    private void HandleKeyboard()
    {
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) || ImGui.IsAnyItemActive()) return;
        var count = visibleRows.Count;
        if (count == 0) { cursor = -1; return; }

        var down = ImGui.IsKeyPressed(ImGuiKey.DownArrow, true);
        var up = ImGui.IsKeyPressed(ImGuiKey.UpArrow, true);
        var toggle = ImGui.IsKeyPressed(ImGuiKey.Space, false);
        var accept = ImGui.IsKeyPressed(ImGuiKey.Enter, false);
        var cancel = ImGui.IsKeyPressed(ImGuiKey.Escape, false);

        if (config.GamepadNavigation)
        {
            down |= gamepad.Pressed(GamepadButtons.DpadDown) > 0;
            up |= gamepad.Pressed(GamepadButtons.DpadUp) > 0;
            toggle |= gamepad.Pressed(GamepadButtons.South) > 0;
            accept |= gamepad.Pressed(GamepadButtons.West) > 0;
            cancel |= gamepad.Pressed(GamepadButtons.East) > 0;
        }

        if (down) cursor = Math.Min(count - 1, cursor + 1);
        if (up) cursor = Math.Max(0, cursor - 1);
        if (toggle && cursor >= 0 && cursor < count)
        {
            var row = visibleRows[cursor];
            if (row.IsExecutable) { row.Checked = !row.Checked; if (!row.Checked) coordinator.SessionSkips.Add(row.Key); }
        }
        if (accept && !coordinator.IsRunning) _ = coordinator.AcceptAsync();
        if (cancel) IsOpen = false;
    }
}
