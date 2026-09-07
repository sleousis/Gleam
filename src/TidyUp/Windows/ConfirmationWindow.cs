using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.GamePad;
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
public sealed class ConfirmationWindow : Window
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
                    Ui.Gap(3);
                    DrawCentered(plan.AllRows.Any() ? "Nothing matches your search." : "Nothing to clean. Everything looks tidy.");
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
        using (ImRaii.PushColor(ImGuiCol.ChildBg, color * new Vector4(1, 1, 1, 0.10f)))
        using (var c = ImRaii.Child("##banner", new Vector2(0, ImGui.GetFrameHeight() + 6 * Ui.Scale), false, ImGuiWindowFlags.None))
        {
            if (c)
            {
                ImGui.SetCursorPos(new Vector2(Ui.Space, 3 * Ui.Scale));
                ImGui.AlignTextToFramePadding();
                Ui.TextColored(color, "Last run:");
                ImGui.SameLine();
                Ui.Text(string.Join(" · ", parts));
                ImGui.SameLine();
                Ui.RightAlign(60 * Ui.Scale);
                if (Ui.LinkButton("Dismiss")) bannerDismissed = true;
            }
        }
    }

    // ---------- top bar: search, filter menu, rescan ----------

    private static readonly IReadOnlyList<(Core.Rules.PresetName, string)> PresetOptions =
    [
        (Core.Rules.PresetName.Cautious, "Cautious"), (Core.Rules.PresetName.Balanced, "Balanced"), (Core.Rules.PresetName.Aggressive, "Aggressive"),
    ];

    private void DrawTopBar(RunPlan plan)
    {
        // Preset first: what this list will do is the most important thing on the screen.
        var profile = coordinator.EffectiveProfile;
        var preset = Core.Rules.Presets.Detect(profile.Thresholds);
        if (Ui.Segmented("##preset", ref preset, PresetOptions))
        {
            if (config.Profiles.IsOverridden(plan.CharacterId, nameof(Core.Lists.Profile.Thresholds)))
                config.Profiles.GetOrCreateOverride(plan.CharacterId, plan.CharacterName).Values.ApplyPreset(preset);
            else
                config.Profiles.Account.ApplyPreset(preset);
            config.Save(PluginServices.PluginInterface);
            _ = coordinator.RefreshPlanAsync(openWindow: false, coordinator.FocusContainer);
        }
        ImGui.SameLine();
        Ui.TextColored(Ui.Accent, profile.Thresholds.Policy.Describe());
        Ui.Gap(0.3f);

        ImGui.SetNextItemWidth(260 * Ui.Scale);
        Ui.InputText("##search", "Search", ref search, 64);

        ImGui.SameLine();
        var filtersActive = filterContainer is not null || filterRule is not null || filterAction is not null || sortMode != 0;
        if (Ui.Button(filtersActive ? "Filter •" : "Filter")) ImGui.OpenPopup("##filters", ImGuiPopupFlags.None);
        DrawFilterMenu();

        ImGui.SameLine();
        if (Ui.LinkButton("Rescan")) _ = coordinator.RefreshPlanAsync(openWindow: false, coordinator.FocusContainer);

        var checkedCount = plan.AllRows.Count(r => r.Checked && r.IsExecutable);
        var total = plan.AllRows.Count(r => r.IsExecutable);
        var label = coordinator.FocusContainer is { } fc ? $"{fc.DisplayName()} · {checkedCount} of {total} selected" : $"{checkedCount} of {total} selected";
        var w = ImGui.CalcTextSize(label, false, 0).X + 130 * Ui.Scale;
        ImGui.SameLine();
        Ui.RightAlign(w);
        Ui.Hint(label);
        ImGui.SameLine();
        if (Ui.LinkButton(checkedCount == total ? "None" : "All"))
        {
            var toAll = checkedCount != total;
            foreach (var r in plan.AllRows.Where(r => r.IsExecutable))
            {
                r.Checked = toAll;
                if (!toAll) coordinator.SessionSkips.Add(r.Key); else coordinator.SessionSkips.Remove(r.Key);
            }
        }
        Ui.Tooltip("All selects every row, including ones that started unchecked because of a warning. Review those before accepting.");
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

        ImGui.Separator();
        Ui.Hint("Action");
        if (ImGui.MenuItem("All actions", string.Empty, filterAction is null, true)) filterAction = null;
        foreach (var a in new[] { ActionKind.Discard, ActionKind.VendorSell, ActionKind.ExpertDelivery, ActionKind.Desynth, ActionKind.None })
            if (ImGui.MenuItem(a.Label(), string.Empty, filterAction == a, true)) filterAction = a;

        ImGui.Separator();
        Ui.Hint("Sort");
        if (ImGui.MenuItem("Name", string.Empty, sortMode == 0, true)) sortMode = 0;
        if (ImGui.MenuItem("Value", string.Empty, sortMode == 1, true)) sortMode = 1;
        if (ImGui.MenuItem("Quantity", string.Empty, sortMode == 2, true)) sortMode = 2;

        ImGui.Separator();
        if (ImGui.MenuItem("Reset", string.Empty, false, true)) { filterContainer = null; filterRule = null; filterAction = null; sortMode = 0; }
    }

    private IEnumerable<PlanRow> Filter(IEnumerable<PlanRow> rows)
    {
        var q = rows;
        if (filterContainer is { } c) q = q.Where(r => r.Item.Slot.Kind == c);
        if (filterRule is { } rule) q = q.Where(r => r.Proposal.RuleId == rule);
        if (filterAction is { } a) q = q.Where(r => r.ChosenAction == a);
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
        var expanded = ImGui.CollapsingHeader($"{section.Title}  ·  {rows.Count}###{key}", ImGuiTreeNodeFlags.None);
        sectionOpen[key] = expanded;

        // Status chip on the same line, right-aligned.
        var chip = section.IsAvailableNow ? "ready" : $"needs: {section.Requirement}";
        if (!section.IsAvailableNow && section.Kind == ContainerKind.GlamourDresser) chip += $" · {section.FreeSlotsNeeded} free bag slots";
        ImGui.SameLine();
        Ui.RightAlign(ImGui.CalcTextSize(chip, false, 0).X + 24 * Ui.Scale);
        Ui.Pill(chip, section.IsAvailableNow ? Ui.Ok : Ui.Warn);
        if (!expanded) return;

        using var table = ImRaii.Table($"##t{key}", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.PadOuterX);
        if (!table) return;
        ImGui.TableSetupColumn("##chk", ImGuiTableColumnFlags.WidthFixed, 24 * Ui.Scale, 0);
        ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, 30 * Ui.Scale, 0);
        ImGui.TableSetupColumn("##item", ImGuiTableColumnFlags.WidthStretch, 5f, 0);
        ImGui.TableSetupColumn("##action", ImGuiTableColumnFlags.WidthFixed, 150 * Ui.Scale, 0);
        ImGui.TableSetupColumn("##why", ImGuiTableColumnFlags.WidthStretch, 5f, 0);

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
        var tex = icons.Get(row.Info.IconId, row.Item.IsHq);
        if (!tex.IsNull) ImGui.Image(tex, new Vector2(26 * Ui.Scale, 26 * Ui.Scale));

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        var name = row.Info.Name + (row.Item.IsHq ? " " : "");
        ImGui.Selectable(name, index == cursor, ImGuiSelectableFlags.AllowItemOverlap, Vector2.Zero);
        if (ImGui.IsItemHovered()) DrawRowTooltip(row);
        DrawRowContextMenu(row);
        ImGui.SameLine();
        var meta = $"× {row.Item.Quantity}";
        if (row.Item.IsDyed) meta += $"  ·  {db.StainName(row.Item.Stain0)}";
        if (row.Item.HasMateria) meta += $"  ·  {row.Item.MateriaCount} materia";
        Ui.Hint(meta);

        ImGui.TableNextColumn();
        DrawActionPicker(row);

        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        if (row.Proposal.Warnings.Count > 0)
        {
            Ui.TextColored(Ui.Warn, row.Proposal.Warnings[0]);
            if (ImGui.IsItemHovered() && row.Proposal.Warnings.Count > 1) ImGui.SetTooltip(string.Join("\n", row.Proposal.Warnings));
        }
        else
        {
            Ui.Hint(row.Proposal.Reason);
        }
    }

    private void DrawActionPicker(PlanRow row)
    {
        var options = new List<ActionKind> { row.Proposal.Action };
        options.AddRange(row.Proposal.Alternatives.Where(a => a != row.Proposal.Action));
        if (!options.Contains(row.ChosenAction)) options.Insert(0, row.ChosenAction);
        var labels = options.Select(a => a == row.ChosenAction && row.Proposal.ValueLabel != "—" ? $"{a.Label()} · {row.Proposal.ValueLabel}" : a.Label()).ToList();
        var idx = options.IndexOf(row.ChosenAction);
        ImGui.SetNextItemWidth(-1);
        using var c = ImRaii.PushColor(ImGuiCol.Text, Ui.ActionColor(row.ChosenAction));
        using var bg = ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Zero);
        if (Ui.Combo("##act", ref idx, labels))
        {
            row.ChosenAction = options[idx];
            if (!row.IsExecutable) row.Checked = false;
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
        ImGui.Separator();
        var summary = plan.Summarize();
        var t = coordinator.EffectiveProfile.Thresholds;
        var rowsForCap = coordinator.FocusContainer is { } f ? plan.Sections.Where(s => s.Kind == f).SelectMany(s => s.Rows) : plan.AllRows;
        var cap = SoftCap.Evaluate(rowsForCap, t);

        var freed = summary.SlotsFreedByContainer.Values.Sum();
        var parts = new List<string>();
        if (freed > 0) parts.Add($"frees {freed} slot{(freed == 1 ? "" : "s")}");
        if (summary.GilRecovered > 0) parts.Add($"recovers {Ui.Gil(summary.GilRecovered)}");
        if (summary.GilDestroyed > 0) parts.Add($"destroys {Ui.Gil(summary.GilDestroyed)} of vendor value");
        if (summary.SealsRows > 0) parts.Add($"{summary.SealsRows} to seals");
        if (coordinator.PendingActions.Count > 0) parts.Add($"{coordinator.PendingActions.Count} accepted earlier still waiting");

        ImGui.AlignTextToFramePadding();
        Ui.Hint(parts.Count == 0 ? "Select rows to see what this run would do." : string.Join("  ·  ", parts));

        var handsFree = Pilot is not null && config.Automation.Enabled && coordinator.FocusContainer is null && !(Pilot?.IsRunning ?? false);
        var needsTravel = plan.AllRows.Any(r => r.Checked && r.IsExecutable && (!r.Item.Slot.Kind.IsAlwaysLoaded() || r.ChosenAction == ActionKind.VendorSell));
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
        Ui.Gap(2);
        DrawCentered("Hands-free run");
        Ui.Gap(0.5f);
        DrawCentered(Pilot!.Status, muted: true);
        if (coordinator.IsRunning)
        {
            var frac = coordinator.RunTotal == 0 ? 0f : (float)coordinator.RunDone / coordinator.RunTotal;
            ImGui.SetCursorPosX(ImGui.GetWindowWidth() * 0.2f);
            using (ImRaii.PushColor(ImGuiCol.PlotHistogram, Ui.Accent))
                ImGui.ProgressBar(frac, new Vector2(ImGui.GetWindowWidth() * 0.6f, 0), $"{coordinator.RunDone} / {coordinator.RunTotal}");
        }
        Ui.Gap();
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - 120 * Ui.Scale) / 2);
        if (Ui.PrimaryButton("Stop", 120 * Ui.Scale, danger: true)) Pilot.Stop();
        DrawCentered("Stops moving and finishes only the current item.", muted: true);
    }

    private void DrawRunning()
    {
        Ui.Gap(2);
        DrawCentered("Cleaning…");
        var frac = coordinator.RunTotal == 0 ? 0f : (float)coordinator.RunDone / coordinator.RunTotal;
        ImGui.SetCursorPosX(ImGui.GetWindowWidth() * 0.2f);
        using (ImRaii.PushColor(ImGuiCol.PlotHistogram, Ui.Accent))
            ImGui.ProgressBar(frac, new Vector2(ImGui.GetWindowWidth() * 0.6f, 0), $"{coordinator.RunDone} / {coordinator.RunTotal}");
        if (coordinator.LastProgress is { } p) DrawCentered($"{p.Action.ItemName} × {p.Action.Quantity} · {p.Message}", muted: true);
        Ui.Gap();
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - 120 * Ui.Scale) / 2);
        if (Ui.PrimaryButton("Stop", 120 * Ui.Scale, danger: true)) coordinator.CancelRun();
        DrawCentered("Stopping finishes the current item and leaves the rest untouched.", muted: true);
    }

    private static void DrawCentered(string text, bool muted = false)
    {
        var w = ImGui.CalcTextSize(text, false, 0).X;
        ImGui.SetCursorPosX(Math.Max(0, (ImGui.GetWindowWidth() - w) / 2));
        if (muted) Ui.Hint(text); else Ui.Text(text);
    }

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
