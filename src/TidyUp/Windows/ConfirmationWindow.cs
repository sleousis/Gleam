using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using TidyUp.Core.Model;
using TidyUp.Core.Planning;
using TidyUp.Game;
using TidyUp.Services;

namespace TidyUp.Windows;

/// <summary>The contract window: everything shown here is exactly what Accept will act on.</summary>
public sealed class ConfirmationWindow : Window
{
    private readonly RunCoordinator coordinator;
    private readonly IconCache icons;
    private readonly ItemDatabase db;
    private readonly Configuration config;
    private readonly IGamepadState gamepad;
    private readonly Action openSettings;

    private string search = string.Empty;
    private int filterContainer; // 0 = all
    private int filterRule;      // 0 = all
    private int filterAction;    // 0 = all
    private int sortMode;        // 0 name, 1 value desc, 2 qty desc, 3 rule
    private bool capArmed;
    private int cursor = -1;
    private readonly List<PlanRow> visibleRows = new();
    private readonly Dictionary<string, bool> sectionOpen = new();
    private static readonly string[] SortNames = ["Name", "Value", "Quantity", "Rule"];

    public ConfirmationWindow(RunCoordinator coordinator, IconCache icons, ItemDatabase db, Configuration config, IGamepadState gamepad, Action openSettings)
        : base("Tidy Up###TidyUpConfirm")
    {
        this.coordinator = coordinator;
        this.icons = icons;
        this.db = db;
        this.config = config;
        this.gamepad = gamepad;
        this.openSettings = openSettings;
        Size = new Vector2(980, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(640, 360), MaximumSize = new Vector2(4000, 3000) };
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = Dalamud.Interface.FontAwesomeIcon.Cog,
            Click = _ => openSettings(),
            ShowTooltip = () => ImGui.SetTooltip("Tidy Up settings"),
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
        if (coordinator.IsRunning) { DrawRunning(); return; }
        if (plan is null)
        {
            Ui.Muted2(string.IsNullOrEmpty(coordinator.Status) ? "Scanning…" : coordinator.Status);
            return;
        }

        DrawToolbar(plan);
        ImGui.Separator();

        var footerHeight = ImGui.GetFrameHeight() * 2.6f;
        using (var child = ImRaii.Child("##rows", new Vector2(0, -footerHeight), false, ImGuiWindowFlags.None))
        {
            if (child)
            {
                visibleRows.Clear();
                var sections = coordinator.FocusContainer is { } f ? plan.Sections.Where(s => s.Kind == f) : plan.Sections;
                var any = false;
                foreach (var section in sections.OrderBy(s => s.Kind.ExecutionOrder()).ThenBy(s => s.OwnerName))
                {
                    var rows = Filter(section.Rows).ToList();
                    if (rows.Count == 0) continue;
                    any = true;
                    DrawSection(section, rows);
                }
                if (coordinator.FocusContainer is null && plan.Alts.Count > 0) DrawAlts(plan);
                if (!any)
                {
                    ImGui.Spacing();
                    Ui.TextColored(Ui.Ok, plan.AllRows.Any() ? "Nothing matches the current filter." : "Nothing to clean. Your containers are already tidy.");
                }
                DrawExcludedNote(plan);
            }
        }

        HandleKeyboard();
        DrawFooter(plan);
    }

    // ---------- toolbar ----------

    private void DrawToolbar(RunPlan plan)
    {
        var profile = coordinator.EffectiveProfile;
        var preset = profile.Preset;
        ImGui.SetNextItemWidth(120 * Ui.Scale);
        if (Ui.ComboEnum("##preset", ref preset))
        {
            var effective = config.Profiles.Effective(plan.CharacterId);
            if (config.Profiles.IsOverridden(plan.CharacterId, nameof(Core.Lists.Profile.Thresholds)))
            {
                var o = config.Profiles.GetOrCreateOverride(plan.CharacterId, plan.CharacterName);
                o.Values.ApplyPreset(preset);
            }
            else
            {
                config.Profiles.Account.ApplyPreset(preset);
            }
            config.Save(PluginServices.PluginInterface);
            _ = coordinator.RefreshPlanAsync(openWindow: false, coordinator.FocusContainer);
        }
        Ui.Tooltip("Preset. Changes thresholds and re-scans.");

        ImGui.SameLine();
        ImGui.SetNextItemWidth(220 * Ui.Scale);
        Ui.InputText("##search", "Search items…", ref search, 64);

        ImGui.SameLine();
        var containers = new List<string> { "All containers" };
        containers.AddRange(Enum.GetValues<ContainerKind>().Select(k => k.DisplayName()));
        ImGui.SetNextItemWidth(150 * Ui.Scale);
        Ui.Combo("##fc", ref filterContainer, containers);

        ImGui.SameLine();
        var rules = new List<string> { "All rules" };
        rules.AddRange(Core.Rules.RuleEngine.AllRules.Select(r => r.Name));
        rules.Add("Always-discard list");
        ImGui.SetNextItemWidth(190 * Ui.Scale);
        Ui.Combo("##fr", ref filterRule, rules);

        ImGui.SameLine();
        var actionsList = new List<string> { "All actions" };
        actionsList.AddRange(Enum.GetValues<ActionKind>().Select(a => a.Label()));
        ImGui.SetNextItemWidth(110 * Ui.Scale);
        Ui.Combo("##fa", ref filterAction, actionsList);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(100 * Ui.Scale);
        Ui.Combo("##sort", ref sortMode, SortNames);
        Ui.Tooltip("Sort within each container");

        ImGui.SameLine();
        if (Ui.Button("Deselect all")) foreach (var r in plan.AllRows) r.Checked = false;
        ImGui.SameLine();
        if (Ui.Button("Select confident")) foreach (var r in plan.AllRows) r.Checked = r.Proposal.DefaultChecked && !coordinator.SessionSkips.Contains(r.Key);
        ImGui.SameLine();
        if (Ui.Button("Rescan")) _ = coordinator.RefreshPlanAsync(openWindow: false, coordinator.FocusContainer);
    }

    private IEnumerable<PlanRow> Filter(IEnumerable<PlanRow> rows)
    {
        var q = rows;
        if (filterContainer > 0) q = q.Where(r => (int)r.Item.Slot.Kind == filterContainer - 1);
        if (filterRule > 0)
        {
            var all = Core.Rules.RuleEngine.AllRules;
            q = filterRule <= all.Count
                ? q.Where(r => r.Proposal.RuleId == all[filterRule - 1].Id)
                : q.Where(r => r.Proposal.RuleId == "always-discard");
        }
        if (filterAction > 0) q = q.Where(r => (int)r.ChosenAction == filterAction - 1);
        if (!string.IsNullOrWhiteSpace(search)) q = q.Where(r => r.Info.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || r.Proposal.Reason.Contains(search, StringComparison.OrdinalIgnoreCase));
        return sortMode switch
        {
            1 => q.OrderByDescending(r => r.Proposal.ValueGil),
            2 => q.OrderByDescending(r => r.Item.Quantity),
            3 => q.OrderBy(r => r.Proposal.RuleId).ThenBy(r => r.Info.Name),
            _ => q.OrderBy(r => r.Info.Name, StringComparer.OrdinalIgnoreCase),
        };
    }

    // ---------- sections ----------

    private void DrawSection(PlanSection section, List<PlanRow> rows)
    {
        var key = $"{section.Kind}:{section.OwnerId}";
        var defaultOpen = !(section.Kind == ContainerKind.Retainer && coordinator.EffectiveProfile.RetainerSectionsCollapsed);
        if (!sectionOpen.TryGetValue(key, out var open)) open = defaultOpen;

        var checkedCount = section.CheckedCount;
        var title = $"{section.Title} ({rows.Count})";
        ImGui.SetNextItemOpen(open, ImGuiCond.Always);
        var expanded = ImGui.CollapsingHeader($"{title}###{key}", ImGuiTreeNodeFlags.None);
        sectionOpen[key] = expanded;

        ImGui.SameLine(ImGui.GetWindowWidth() - 260 * Ui.Scale);
        if (section.IsAvailableNow) Ui.TextColored(Ui.Ok, checkedCount > 0 ? $"ready · {checkedCount} selected" : "ready");
        else
        {
            var need = section.Kind == ContainerKind.GlamourDresser ? $" · {section.FreeSlotsNeeded} free inventory slots" : string.Empty;
            Ui.TextColored(Ui.Warn, $"needs: {section.Requirement}{need}");
        }
        if (!expanded) return;

        using var table = ImRaii.Table($"##t{key}", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings);
        if (!table) return;
        ImGui.TableSetupColumn("##chk", ImGuiTableColumnFlags.WidthFixed, 26 * Ui.Scale, 0);
        ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, 30 * Ui.Scale, 0);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 3f, 0);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 44 * Ui.Scale, 0);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 110 * Ui.Scale, 0);
        ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 90 * Ui.Scale, 0);
        ImGui.TableSetupColumn("Why", ImGuiTableColumnFlags.WidthStretch, 4f, 0);
        ImGui.TableHeadersRow();

        foreach (var row in rows)
        {
            var index = visibleRows.Count;
            visibleRows.Add(row);
            DrawRow(row, index);
        }
    }

    private void DrawRow(PlanRow row, int index)
    {
        using var id = ImRaii.PushId(row.Key);
        ImGui.TableNextRow();
        if (index == cursor) ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(ImGuiCol.HeaderHovered));

        ImGui.TableNextColumn();
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
        if (!tex.IsNull) ImGui.Image(tex, new Vector2(24 * Ui.Scale, 24 * Ui.Scale));

        ImGui.TableNextColumn();
        var name = row.Info.Name;
        if (row.Item.IsHq) name += " ";
        if (row.Item.IsDyed) name += $"  ({db.StainName(row.Item.Stain0)}{(row.Item.Stain1 != 0 ? ", " + db.StainName(row.Item.Stain1) : "")})";
        if (row.Item.HasMateria) name += $"  ⚙{row.Item.MateriaCount}";
        ImGui.Selectable(name, index == cursor, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap, Vector2.Zero);
        if (ImGui.IsItemHovered()) DrawRowTooltip(row);
        DrawRowContextMenu(row);

        ImGui.TableNextColumn();
        Ui.Text($"× {row.Item.Quantity}");

        ImGui.TableNextColumn();
        DrawActionPicker(row);

        ImGui.TableNextColumn();
        Ui.Text(row.Proposal.ValueLabel);

        ImGui.TableNextColumn();
        var reasonColor = row.Proposal.Warnings.Count > 0 ? Ui.Warn : Ui.ConfidenceColor(row.Proposal.Confidence);
        Ui.TextColored(reasonColor, row.Proposal.Reason);
        if (row.Proposal.Warnings.Count > 0)
        {
            ImGui.SameLine();
            Ui.TextColored(Ui.Warn, $"· {string.Join(" · ", row.Proposal.Warnings)}");
        }
    }

    private void DrawActionPicker(PlanRow row)
    {
        var options = new List<ActionKind> { row.Proposal.Action };
        options.AddRange(row.Proposal.Alternatives.Where(a => a != row.Proposal.Action));
        if (!options.Contains(row.ChosenAction)) options.Insert(0, row.ChosenAction);
        var labels = options.Select(a => a.Label()).ToList();
        var idx = options.IndexOf(row.ChosenAction);
        ImGui.SetNextItemWidth(-1);
        using var c = ImRaii.PushColor(ImGuiCol.Text, Ui.ActionColor(row.ChosenAction));
        if (Ui.Combo("##act", ref idx, labels))
        {
            row.ChosenAction = options[idx];
            if (!row.IsExecutable) row.Checked = false;
        }
    }

    private void DrawRowTooltip(PlanRow row)
    {
        using var t = ImRaii.Tooltip();
        Ui.TextColored(Ui.Gold, row.Info.Name);
        Ui.Muted2($"{row.Info.UiCategory} · iL{row.Info.ItemLevel} · lv{row.Info.LevelEquip} · id {row.Info.ItemId}");
        Ui.Text($"Slot: {row.Item.Slot}");
        Ui.Text($"Vendor: {Ui.Gil(row.Info.VendorPrice)} each · {(row.Info.IsMarketable ? "marketable" : "not marketable")}{(row.Info.IsUntradable ? " · untradeable" : "")}{(row.Info.IsUnique ? " · unique" : "")}");
        Ui.Text($"Rule: {row.Proposal.RuleId} · confidence {row.Proposal.Confidence}");
        if (row.Proposal.Alternatives.Count > 0) Ui.Muted2($"Alternatives: {string.Join(", ", row.Proposal.Alternatives.Select(a => a.Label()))}");
        foreach (var w in row.Proposal.Warnings) Ui.TextColored(Ui.Warn, w);
        Ui.Muted2("Right-click for options");
    }

    private void DrawRowContextMenu(PlanRow row)
    {
        using var popup = ImRaii.ContextPopupItem("##ctx");
        if (!popup) return;
        Ui.TextColored(Ui.Gold, row.Info.Name);
        ImGui.Separator();
        if (ImGui.MenuItem("Skip this time", string.Empty, false, true)) coordinator.SkipRow(row);
        if (ImGui.MenuItem("Never discard (protect list)", string.Empty, false, true)) coordinator.Protect(row.Info.ItemId, row.Info.Name);
        if (ImGui.MenuItem("Always discard", string.Empty, false, true)) coordinator.AlwaysDiscard(row.Info.ItemId, row.Info.Name);
        ImGui.Separator();
        if (ImGui.MenuItem("Garland Tools", string.Empty, false, true)) Ui.OpenLink(Ui.GarlandUrl(row.Info.ItemId));
        if (ImGui.MenuItem("Universalis", string.Empty, false, true)) Ui.OpenLink(Ui.UniversalisUrl(row.Info.ItemId));
        if (ImGui.MenuItem("Console Games Wiki", string.Empty, false, true)) Ui.OpenLink(Ui.WikiUrl(row.Info.Name));
    }

    private void DrawAlts(RunPlan plan)
    {
        foreach (var alt in plan.Alts)
        {
            var key = $"alt:{alt.CharacterId}";
            if (!sectionOpen.TryGetValue(key, out var open)) open = false;
            ImGui.SetNextItemOpen(open, ImGuiCond.Always);
            var expanded = ImGui.CollapsingHeader($"{alt.CharacterName}: {alt.Proposals.Count} cleanable###{key}", ImGuiTreeNodeFlags.None);
            sectionOpen[key] = expanded;
            ImGui.SameLine(ImGui.GetWindowWidth() - 260 * Ui.Scale);
            Ui.Muted2("read-only · log in as them to act");
            if (!expanded) continue;
            using var table = ImRaii.Table($"##alt{alt.CharacterId}", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings);
            if (!table) continue;
            ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, 30 * Ui.Scale, 0);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 3f, 0);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 44 * Ui.Scale, 0);
            ImGui.TableSetupColumn("Why", ImGuiTableColumnFlags.WidthStretch, 4f, 0);
            foreach (var p in alt.Proposals.OrderBy(p => p.Info.Name))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var tex = icons.Get(p.Info.IconId, p.Item.IsHq);
                if (!tex.IsNull) ImGui.Image(tex, new Vector2(24 * Ui.Scale, 24 * Ui.Scale));
                ImGui.TableNextColumn(); Ui.Text(p.Info.Name);
                ImGui.TableNextColumn(); Ui.Text($"× {p.Item.Quantity}");
                ImGui.TableNextColumn(); Ui.Muted2($"{p.Item.Slot.Kind.DisplayName()} · {p.Reason}");
            }
        }
    }

    private static void DrawExcludedNote(RunPlan plan)
    {
        if (plan.Excluded.Count == 0) return;
        ImGui.Spacing();
        var hard = plan.Excluded.Count(e => e.IsHardBlock);
        var prot = plan.Excluded.Count - hard;
        Ui.Muted2($"Not shown: {hard} hard-blocked (gearsets, plates, unique, indisposable) · {prot} on your protect list");
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

        var freed = string.Join(" · ", summary.SlotsFreedByContainer.Select(kv => $"{kv.Value} {kv.Key.DisplayName().ToLowerInvariant()}"));
        var line = $"{cap.Items} selected";
        if (freed.Length > 0) line += $" · frees {freed}";
        if (summary.GilRecovered > 0) line += $" · recovers {Ui.Gil(summary.GilRecovered)}";
        if (summary.GilDestroyed > 0) line += $" · destroys {Ui.Gil(summary.GilDestroyed)} of vendor value";
        if (summary.SealsRows > 0) line += $" · {summary.SealsRows} to seals";
        if (summary.DesynthRows > 0) line += $" · {summary.DesynthRows} to desynth";
        Ui.Text(line);
        if (coordinator.PendingActions.Count > 0)
        {
            ImGui.SameLine();
            Ui.TextColored(Ui.Warn, $"· {coordinator.PendingActions.Count} accepted earlier still waiting for their container");
        }

        var verb = coordinator.FocusContainer is null ? "Accept & Clean" : "Accept & Clean here";
        var label = cap.Exceeded && !capArmed ? cap.ButtonLabel(verb) : $"{verb} {cap.Items}";
        var canAccept = cap.Items > 0;

        ImGui.SetCursorPosX(ImGui.GetWindowWidth() - 330 * Ui.Scale);
        if (Ui.Button("Cancel", 90 * Ui.Scale)) { IsOpen = false; }
        ImGui.SameLine();
        using (ImRaii.Disabled(!canAccept))
        {
            if (Ui.ButtonColored(label, cap.Exceeded && !capArmed ? Ui.Warn : Ui.Gold, 220 * Ui.Scale))
            {
                if (cap.Exceeded && !capArmed) capArmed = true;
                else { capArmed = false; _ = coordinator.AcceptAsync(); }
            }
        }
        if (cap.Exceeded) Ui.Tooltip(cap.Explanation);
    }

    private void DrawRunning()
    {
        Ui.TextColored(Ui.Gold, "Cleaning…");
        var frac = coordinator.RunTotal == 0 ? 0f : (float)coordinator.RunDone / coordinator.RunTotal;
        ImGui.ProgressBar(frac, new Vector2(-1, 0), $"{coordinator.RunDone} / {coordinator.RunTotal}");
        if (coordinator.LastProgress is { } p)
            Ui.Muted2($"{p.Action.ItemName} × {p.Action.Quantity}: {p.Outcome} {p.Message}");
        ImGui.Spacing();
        if (Ui.ButtonColored("Stop", Ui.Danger, 120 * Ui.Scale)) coordinator.CancelRun();
        Ui.Muted2("Stopping finishes the current item and leaves the rest untouched.");
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
