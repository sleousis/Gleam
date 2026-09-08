using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using TidyUp.Core.Model;
using TidyUp.Core.Organizer.Capacity;
using TidyUp.Core.Organizer.Model;
using TidyUp.Core.Organizer.Solving;
using TidyUp.Game;
using TidyUp.Services;

namespace TidyUp.Windows;

/// <summary>
/// The organizer: a layout (ordered rules → destinations) on one side, the preview of what would move on
/// the other. Nothing moves until the player presses Organize on a preview that fits.
/// </summary>
public sealed class OrganizerWindow : StyledWindow
{
    private readonly OrganizerCoordinator organizer;
    private readonly Configuration config;
    private readonly ItemDatabase db;
    private readonly IconCache icons;
    private readonly Action save;

    private enum View { Preview, Rules }
    private View view = View.Preview;
    private Guid? selectedRule;
    private string itemSearch = string.Empty;
    private List<ItemInfo> itemResults = new();
    private bool dirty;
    private bool confirmDelete;

    private static readonly IReadOnlyList<(View, string)> Views = [(View.Preview, "Preview"), (View.Rules, "Rules")];

    /// <summary>Set by the plugin when hands-free mode is available.</summary>
    public Automation.AutoPilot? Pilot { get; set; }

    public OrganizerWindow(OrganizerCoordinator organizer, Configuration config, ItemDatabase db, IconCache icons, Action save)
        : base("Tidy Up Organizer###TidyUpOrganizer")
    {
        this.organizer = organizer;
        this.config = config;
        this.db = db;
        this.icons = icons;
        this.save = save;
        Size = new Vector2(900, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(620, 380), MaximumSize = new Vector2(4000, 3000) };
    }

    public override void OnOpen()
    {
        if (organizer.Current is null && !organizer.IsPreviewing) _ = organizer.PreviewAsync();
    }

    private OrganizerPlan? Plan => config.Organizer.Active;

    public override void Draw()
    {
        var plan = Plan;
        var subtitle = plan is null ? "No layout yet" : $"{plan.Name} · {plan.Rules.Count(r => r.Enabled)} rule{(plan.Rules.Count == 1 ? "" : "s")}";
        Ui.Header(icons.Logo, "Organize", subtitle, Ui.SegmentedWidth(Views), () =>
        {
            if (Ui.Segmented("##view", ref view, Views) && view == View.Preview && organizer.Current is null) _ = organizer.PreviewAsync();
        });

        DrawPlanBar();
        Ui.Gap(0.4f);

        if (Pilot is { IsRunning: true } || organizer.IsRunning) { DrawRunning(); return; }

        var footer = ImGui.GetFrameHeight() * 2.4f + Ui.Space;
        using (var body = ImRaii.Child("##body", new Vector2(0, view == View.Preview ? -footer : 0), false, ImGuiWindowFlags.None))
        {
            if (body)
            {
                if (plan is null) Ui.EmptyState(icons.Logo, "No layout yet.", "Press New to start one.");
                else if (view == View.Rules) DrawRules(plan);
                else DrawPreview(plan);
            }
        }
        if (view == View.Preview) DrawFooter();

        if (dirty)
        {
            dirty = false;
            save();
            if (view == View.Preview) _ = organizer.PreviewAsync();
        }
    }

    // ---------- plan bar ----------

    private void DrawPlanBar()
    {
        var plans = config.Organizer.Plans;
        var active = Plan;
        var idx = active is null ? -1 : plans.IndexOf(active);
        ImGui.SetNextItemWidth(220 * Ui.Scale);
        using (ImRaii.PushColor(ImGuiCol.FrameBg, new Vector4(1, 1, 1, 0.06f)))
        {
            if (Ui.Combo("##plan", ref idx, plans.Select(p => p.Name).ToList()) && idx >= 0 && idx < plans.Count)
            {
                config.Organizer.ActivePlanId = plans[idx].Id;
                dirty = true;
            }
        }
        ImGui.SameLine();
        if (Ui.IconButton(FontAwesomeIcon.Plus, "New"))
        {
            var p = new OrganizerPlan { Name = $"Layout {plans.Count + 1}" };
            plans.Add(p);
            config.Organizer.ActivePlanId = p.Id;
            view = View.Rules;
            dirty = true;
        }
        if (active is not null)
        {
            ImGui.SameLine();
            if (Ui.LinkButton("Duplicate"))
            {
                var c = active.Clone();
                c.Name = $"{active.Name} copy";
                plans.Add(c);
                config.Organizer.ActivePlanId = c.Id;
                dirty = true;
            }
            ImGui.SameLine();
            if (Ui.LinkButton(confirmDelete ? "Really delete?" : "Delete"))
            {
                if (!confirmDelete) confirmDelete = true;
                else
                {
                    plans.Remove(active);
                    config.Organizer.ActivePlanId = plans.FirstOrDefault()?.Id;
                    confirmDelete = false;
                    dirty = true;
                }
            }
            if (!ImGui.IsItemHovered() && confirmDelete && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) confirmDelete = false;
        }
    }

    // ---------- rules ----------

    private void DrawRules(OrganizerPlan plan)
    {
        using (Ui.Card("name"))
        {
            var name = plan.Name;
            ImGui.SetNextItemWidth(260 * Ui.Scale);
            if (Ui.InputText("Layout name", "", ref name, 48)) { plan.Name = name; dirty = true; }
            ImGui.SameLine(0, 24 * Ui.Scale);
            Ui.Hint("Everything else");
            ImGui.SameLine();
            var fallback = plan.Fallback;
            if (DestinationCombo("##fallback", ref fallback, allowStay: true)) { plan.Fallback = fallback; dirty = true; }
            Ui.Tooltip("Where items no rule matches go. Stay leaves them where they are.");
        }

        Ui.Hint("Rules are tried from the top; the first one that matches decides.");
        Ui.Gap(0.3f);

        var toRemove = -1;
        for (var i = 0; i < plan.Rules.Count; i++)
        {
            var rule = plan.Rules[i];
            using var id = ImRaii.PushId(rule.Id.ToString());
            var selected = selectedRule == rule.Id;

            using (Ui.Card("rule"))
            {
                var on = rule.Enabled;
                if (ImGui.Checkbox("##on", ref on)) { rule.Enabled = on; dirty = true; }
                Ui.Tooltip(on ? "On. Untick to skip this rule." : "Off.");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(200 * Ui.Scale);
                var name = rule.Name;
                if (Ui.InputText("##name", "Rule name", ref name, 48)) { rule.Name = name; dirty = true; }
                ImGui.SameLine();
                Ui.Icon(FontAwesomeIcon.ArrowRight, Ui.Muted);
                ImGui.SameLine();
                var dest = rule.Then;
                if (DestinationCombo("##dest", ref dest, allowStay: true)) { rule.Then = dest; dirty = true; }

                ImGui.SameLine();
                Ui.RightAlign(150 * Ui.Scale);
                using (ImRaii.Disabled(i == 0)) { if (Ui.LinkButton("Up")) { (plan.Rules[i - 1], plan.Rules[i]) = (plan.Rules[i], plan.Rules[i - 1]); dirty = true; } }
                ImGui.SameLine();
                using (ImRaii.Disabled(i == plan.Rules.Count - 1)) { if (Ui.LinkButton("Down")) { (plan.Rules[i + 1], plan.Rules[i]) = (plan.Rules[i], plan.Rules[i + 1]); dirty = true; } }
                ImGui.SameLine();
                if (Ui.LinkButton("Remove")) toRemove = i;

                // Summary line doubles as the expand toggle.
                using (ImRaii.PushColor(ImGuiCol.Text, Ui.Muted))
                {
                    if (ImGui.Selectable($"{(selected ? "▾" : "▸")} {rule.When.Describe()}##sum", false, ImGuiSelectableFlags.None, Vector2.Zero))
                        selectedRule = selected ? null : rule.Id;
                }
                if (selected) DrawPredicateEditor(rule.When);
            }
        }

        if (toRemove >= 0) { plan.Rules.RemoveAt(toRemove); dirty = true; }

        if (Ui.IconButton(FontAwesomeIcon.Plus, "Add rule"))
        {
            var r = new OrganizerRule { Name = "New rule", Then = Destination.Saddlebag };
            plan.Rules.Add(r);
            selectedRule = r.Id;
            dirty = true;
        }

        Ui.Gap(0.6f);
        using (Ui.Card("options"))
        {
            Ui.TextColored(Ui.Muted, "OPTIONS");
            ImGui.Spacing();
            var merge = plan.MergeStacksAtDestination;
            if (ImGui.Checkbox("Merge into stacks already at the destination", ref merge)) { plan.MergeStacksAtDestination = merge; dirty = true; }
            var reserve = plan.BagStagingReserve;
            ImGui.SetNextItemWidth(100 * Ui.Scale);
            if (Ui.InputInt("Bag slots kept free while items pass through", ref reserve)) { plan.BagStagingReserve = Math.Clamp(reserve, 0, 100); dirty = true; }
            Ui.Tooltip("Items going from one retainer to another pass through your bags. This many slots stay free during that.");

            var retainers = organizer.RetainerNames;
            if (retainers.Count > 0)
            {
                Ui.Gap(0.3f);
                Ui.Hint("Retainers this layout may use");
                var all = plan.RetainersInScope.Count == 0;
                if (ImGui.Checkbox("All of them", ref all)) { if (all) plan.RetainersInScope.Clear(); else plan.RetainersInScope.UnionWith(retainers.Keys); dirty = true; }
                if (!all)
                {
                    foreach (var (rid, rname) in retainers.OrderBy(kv => kv.Value))
                    {
                        var inScope = plan.RetainersInScope.Contains(rid);
                        if (ImGui.Checkbox($"{rname}##scope{rid}", ref inScope)) { if (inScope) plan.RetainersInScope.Add(rid); else plan.RetainersInScope.Remove(rid); dirty = true; }
                    }
                }
            }
        }
    }

    private void DrawPredicateEditor(OrganizerPredicate when)
    {
        Ui.Gap(0.3f);
        Ui.Hint("Matches items that are all of the following:");

        // Types
        ImGui.AlignTextToFramePadding();
        Ui.Hint("Type");
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8 * Ui.Scale);
        foreach (var tag in Enum.GetValues<ItemTag>())
        {
            var active = when.Tags?.Contains(tag) == true;
            if (Ui.Chip(tag.Label(), active))
            {
                when.Tags ??= new HashSet<ItemTag>();
                if (!when.Tags.Remove(tag)) when.Tags.Add(tag);
                if (when.Tags.Count == 0) when.Tags = null;
                dirty = true;
            }
            ImGui.SameLine();
        }
        ImGui.NewLine();

        // Flags
        TriState("Quality", ref when, w => w.IsHq, (w, v) => w.IsHq = v, "HQ", "NQ");
        ImGui.SameLine(); TriState("Materia", ref when, w => w.HasMateria, (w, v) => w.HasMateria = v, "with materia", "without");
        ImGui.SameLine(); TriState("Stacks", ref when, w => w.IsStackable, (w, v) => w.IsStackable = v, "stackable", "single");
        TriState("Trade", ref when, w => w.IsUntradable, (w, v) => w.IsUntradable = v, "untradeable", "tradeable");
        ImGui.SameLine(); TriState("Unique", ref when, w => w.IsUnique, (w, v) => w.IsUnique = v, "unique", "not unique");
        ImGui.SameLine(); TriState("Jobs", ref when, w => w.ForJobsPlayed, (w, v) => w.ForJobsPlayed = v, "jobs I play", "jobs I don't");
        TriState("Never touch", ref when, w => w.OnNeverTouchList, (w, v) => w.OnNeverTouchList = v, "on the list", "not on it");

        // Levels
        Range("Item level", ref when, w => w.MinItemLevel, w => w.MaxItemLevel, (w, v) => w.MinItemLevel = v, (w, v) => w.MaxItemLevel = v, 999);
        Range("Equip level", ref when, w => w.MinEquipLevel, w => w.MaxEquipLevel, (w, v) => w.MinEquipLevel = v, (w, v) => w.MaxEquipLevel = v, 100);

        // Named items
        ImGui.AlignTextToFramePadding();
        Ui.Hint("Items");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220 * Ui.Scale);
        if (Ui.InputText("##itemsearch", "Add a specific item…", ref itemSearch, 64))
            itemResults = itemSearch.Length >= 2 ? db.Search(itemSearch, 20).ToList() : new List<ItemInfo>();
        if (itemResults.Count > 0)
        {
            using var child = ImRaii.Child("##results", new Vector2(0, Math.Min(itemResults.Count, 6) * 26 * Ui.Scale), true, ImGuiWindowFlags.None);
            foreach (var r in itemResults)
            {
                Ui.ImageRounded(icons.Get(r.IconId, false), new Vector2(20 * Ui.Scale, 20 * Ui.Scale), 3 * Ui.Scale);
                ImGui.SameLine();
                if (ImGui.Selectable($"{r.Name}##add{r.ItemId}", false, ImGuiSelectableFlags.None, Vector2.Zero))
                {
                    when.ItemIds ??= new HashSet<uint>();
                    when.ItemIds.Add(r.ItemId);
                    itemResults.Clear();
                    itemSearch = string.Empty;
                    dirty = true;
                    break;
                }
            }
        }
        if (when.ItemIds is { Count: > 0 })
        {
            foreach (var id in when.ItemIds.ToList())
            {
                var info = db.Get(id);
                Ui.Pill(info?.Name ?? $"item {id}", Ui.Muted);
                if (ImGui.IsItemClicked()) { when.ItemIds.Remove(id); if (when.ItemIds.Count == 0) when.ItemIds = null; dirty = true; }
                Ui.Tooltip("Click to remove.");
                ImGui.SameLine();
            }
            ImGui.NewLine();
        }
    }

    private void TriState(string label, ref OrganizerPredicate when, Func<OrganizerPredicate, bool?> get, Action<OrganizerPredicate, bool?> set, string yes, string no)
    {
        var options = new[] { "any", yes, no };
        var idx = get(when) switch { null => 0, true => 1, false => 2 };
        ImGui.AlignTextToFramePadding();
        Ui.Hint(label);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130 * Ui.Scale);
        using var bg = ImRaii.PushColor(ImGuiCol.FrameBg, new Vector4(1, 1, 1, 0.06f));
        if (Ui.Combo($"##{label}", ref idx, options)) { set(when, idx switch { 1 => true, 2 => false, _ => null }); dirty = true; }
    }

    private void Range(string label, ref OrganizerPredicate when, Func<OrganizerPredicate, int?> getMin, Func<OrganizerPredicate, int?> getMax,
        Action<OrganizerPredicate, int?> setMin, Action<OrganizerPredicate, int?> setMax, int cap)
    {
        ImGui.AlignTextToFramePadding();
        Ui.Hint(label);
        ImGui.SameLine();
        var min = getMin(when) ?? 0;
        var max = getMax(when) ?? 0;
        ImGui.SetNextItemWidth(90 * Ui.Scale);
        if (Ui.InputInt($"from##{label}", ref min)) { setMin(when, min <= 0 ? null : Math.Min(min, cap)); dirty = true; }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90 * Ui.Scale);
        if (Ui.InputInt($"to##{label}", ref max)) { setMax(when, max <= 0 ? null : Math.Min(max, cap)); dirty = true; }
        Ui.Tooltip("0 means no limit. Only equipment has levels; these never match anything else.");
    }

    private bool DestinationCombo(string id, ref Destination value, bool allowStay)
    {
        var options = new List<(Destination D, string Label)>();
        if (allowStay) options.Add((Destination.Stay, "Stay where it is"));
        options.Add((Destination.Bags, "Bags"));
        options.Add((Destination.Armoury, "Armoury chest"));
        options.Add((Destination.Saddlebag, "Chocobo saddlebag"));
        options.Add((Destination.AnyRetainer, "Any retainer with room"));
        foreach (var (rid, rname) in organizer.RetainerNames.OrderBy(kv => kv.Value)) options.Add((Destination.RetainerNamed(rid), $"Retainer: {rname}"));
        var current = value;
        var idx = options.FindIndex(o => o.D == current);
        if (idx < 0) { options.Add((current, current.Kind == DestinationKind.Retainer ? $"Retainer {current.RetainerId:X}" : current.Kind.ToString())); idx = options.Count - 1; }
        ImGui.SetNextItemWidth(210 * Ui.Scale);
        using var bg = ImRaii.PushColor(ImGuiCol.FrameBg, new Vector4(1, 1, 1, 0.06f));
        if (!Ui.Combo(id, ref idx, options.Select(o => o.Label).ToList())) return false;
        value = options[idx].D;
        return true;
    }

    // ---------- preview ----------

    private void DrawPreview(OrganizerPlan plan)
    {
        var result = organizer.Current;
        if (organizer.IsPreviewing || result is null)
        {
            Ui.EmptyState(icons.Logo, organizer.IsPreviewing ? "Looking through your storage…" : "Nothing previewed yet.", organizer.Status);
            if (!organizer.IsPreviewing && Ui.IconButton(FontAwesomeIcon.Sync, "Preview")) _ = organizer.PreviewAsync();
            return;
        }

        if (!result.Report.Feasible)
        {
            foreach (var s in result.Report.Shortfalls)
                Ui.Banner(Ui.Danger, "Not enough room", $"{Name(s.Storage)} needs {s.Needed} slot{(s.Needed == 1 ? "" : "s")} but has {s.Free} free ({s.Short} short). Most of that comes from the rule \"{s.TopRule}\".", dismissible: false);
            Ui.Gap(0.4f);
        }

        // End state cards
        var states = result.EndState.Where(e => e.UsedBefore != e.UsedAfter || result.Report.Shortfalls.Any(s => s.Storage == e.Storage)).ToList();
        if (states.Count > 0)
        {
            Ui.Hint("After organising");
            var cardW = Math.Max(180 * Ui.Scale, (ImGui.GetContentRegionAvail().X - 8 * Ui.Scale * (Math.Min(states.Count, 4) - 1)) / Math.Min(states.Count, 4));
            var col = 0;
            foreach (var e in states)
            {
                if (col > 0) ImGui.SameLine();
                DrawStateCard(e, cardW, result.Report.Shortfalls.FirstOrDefault(s => s.Storage == e.Storage));
                col = (col + 1) % Math.Min(states.Count, 4);
            }
            ImGui.NewLine();
            Ui.Gap(0.3f);
        }

        if (result.Moves.Count == 0)
        {
            Ui.EmptyState(icons.Logo, "Everything is already where this layout wants it.", result.Pinned.Count + result.NoRoom.Count > 0 ? "Some items were left alone; see below." : null);
        }
        else
        {
            foreach (var group in result.Moves.GroupBy(m => m.RequiresOpen))
            {
                var title = group.Key is { } s ? $"With {Name(s)} open" : "Bags and armoury";
                ImGui.SetNextItemOpen(true, ImGuiCond.Once);
                bool open;
                using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(10 * Ui.Scale, 6 * Ui.Scale)))
                    open = ImGui.CollapsingHeader($"{title}###g{group.Key}", ImGuiTreeNodeFlags.None);
                ImGui.SameLine();
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8 * Ui.Scale);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 5 * Ui.Scale);
                Ui.Pill($"{group.Count()}", Ui.Muted);
                if (!open) continue;
                DrawMoveTable(group.Key?.ToString() ?? "bags", group.ToList());
            }
        }

        var leftAlone = result.Pinned.Count + result.NoRoom.Count;
        if (leftAlone > 0)
        {
            Ui.Gap(0.5f);
            Ui.Hint($"Left alone: {leftAlone} item{(leftAlone == 1 ? "" : "s")}. Hover for why.");
            if (ImGui.IsItemHovered())
            {
                using var t = Ui.RichTooltip(360);
                foreach (var g in result.Pinned.Concat(result.NoRoom).GroupBy(p => p.Reason).OrderByDescending(g => g.Count()).Take(10))
                    Ui.Text($"{g.Count()} × {g.Key}");
            }
        }
    }

    private void DrawStateCard(StorageEndState e, float width, Shortfall? shortfall)
    {
        var pos = ImGui.GetCursorScreenPos();
        var h = ImGui.GetTextLineHeight() * 2 + 26 * Ui.Scale;
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + new Vector2(width, h), ImGui.GetColorU32(new Vector4(1, 1, 1, 0.035f)), Ui.Rounding);
        dl.AddRect(pos, pos + new Vector2(width, h), ImGui.GetColorU32(shortfall is null ? Ui.InkLine : Ui.Danger * new Vector4(1, 1, 1, 0.6f)), Ui.Rounding);
        var pad = 10 * Ui.Scale;
        ImGui.SetCursorScreenPos(pos + new Vector2(pad, 6 * Ui.Scale));
        using (ImRaii.Group())
        {
            Ui.Text(Name(e.Storage));
            var delta = e.UsedAfter - e.UsedBefore;
            var deltaText = delta == 0 ? "no change" : delta > 0 ? $"+{delta}" : $"{delta}";
            Ui.Hint($"{e.UsedBefore} → {e.UsedAfter} of {e.Size} · {deltaText}{(e.SizesAreLive ? "" : " · size assumed")}");
            var frac = e.Size == 0 ? 0f : Math.Clamp(e.UsedAfter / (float)e.Size, 0f, 1f);
            var barW = width - pad * 2;
            var bp = ImGui.GetCursorScreenPos();
            dl.AddRectFilled(bp, bp + new Vector2(barW, 6 * Ui.Scale), ImGui.GetColorU32(new Vector4(1, 1, 1, 0.08f)), 3 * Ui.Scale);
            dl.AddRectFilled(bp, bp + new Vector2(Math.Max(6 * Ui.Scale, barW * frac), 6 * Ui.Scale), ImGui.GetColorU32(shortfall is null ? Ui.Accent : Ui.Danger), 3 * Ui.Scale);
            ImGui.Dummy(new Vector2(barW, 6 * Ui.Scale));
        }
        ImGui.SetCursorScreenPos(new Vector2(pos.X + width, pos.Y));
        ImGui.Dummy(new Vector2(0, h));
    }

    private void DrawMoveTable(string key, List<MoveOp> moves)
    {
        using var table = ImRaii.Table($"##mv{key}", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.PadOuterX);
        if (!table) return;
        ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, 30 * Ui.Scale, 0);
        ImGui.TableSetupColumn("##item", ImGuiTableColumnFlags.WidthStretch, 5f, 0);
        ImGui.TableSetupColumn("##route", ImGuiTableColumnFlags.WidthStretch, 4f, 0);
        ImGui.TableSetupColumn("##leg", ImGuiTableColumnFlags.WidthFixed, 120 * Ui.Scale, 0);

        foreach (var m in moves)
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.None, 28 * Ui.Scale);
            ImGui.TableNextColumn();
            Ui.ImageRounded(icons.Get(m.Info.IconId, m.Item.IsHq), new Vector2(24 * Ui.Scale, 24 * Ui.Scale), 4 * Ui.Scale);
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            Ui.Text(m.Info.Name);
            ImGui.SameLine();
            Ui.Hint($"× {m.Item.Quantity}");
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            Ui.Hint($"{Name(m.From)}  →  {Name(m.To)}");
            ImGui.TableNextColumn();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 3 * Ui.Scale);
            if (m.Leg == MoveLeg.RelayOut) Ui.Pill("via bags · out", Ui.Info);
            else if (m.Leg == MoveLeg.RelayIn) Ui.Pill("via bags · in", Ui.Info);
            if (m.Pass > 1) { ImGui.SameLine(); Ui.Pill($"pass {m.Pass}", Ui.Warn); }
        }
    }

    private void DrawFooter()
    {
        Ui.Rule();
        Ui.Gap(0.3f);
        var r = organizer.Current;
        var parts = new List<string>();
        if (r is not null)
        {
            if (r.Moves.Count > 0) parts.Add($"{r.Moves.Count} move{(r.Moves.Count == 1 ? "" : "s")}");
            if (r.RelayedMoves > 0) parts.Add($"{r.RelayedMoves} via the bags");
            var opens = r.StoragesToOpen.Count();
            if (opens > 0) parts.Add($"{opens} storage{(opens == 1 ? "" : "s")} to open");
            if (r.Passes > 1) parts.Add($"{r.Passes} passes");
            if (organizer.PendingMoves.Count > 0) parts.Add($"{organizer.PendingMoves.Count} approved earlier still waiting");
        }
        ImGui.AlignTextToFramePadding();
        Ui.Hint(parts.Count == 0 ? (string.IsNullOrEmpty(organizer.Status) ? "Preview a layout to see what would move." : organizer.Status) : string.Join("  ·  ", parts));

        var canRun = r is not null && r.Report.Feasible && r.Moves.Count > 0 && !organizer.IsPreviewing;
        var handsFree = Pilot is not null && config.Automation.Enabled && r is not null && r.StoragesToOpen.Any();
        var buttonWidth = 200 * Ui.Scale;
        ImGui.SameLine();
        Ui.RightAlign(buttonWidth + (handsFree ? 300 : 180) * Ui.Scale);
        if (Ui.IconButton(FontAwesomeIcon.Sync, "Preview again")) _ = organizer.PreviewAsync();
        ImGui.SameLine();
        if (Ui.LinkButton("Close")) IsOpen = false;
        if (handsFree)
        {
            ImGui.SameLine();
            using (ImRaii.Disabled(!canRun))
            {
                if (Ui.LinkButton("Organize here only")) _ = organizer.RunAsync();
            }
            Ui.Tooltip("Moves what is reachable right now; the rest waits for you to open its storage.");
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!canRun))
        {
            var label = r is { Report.Feasible: false } ? "Make room first" : handsFree ? $"Organize {r!.Moves.Count} everywhere" : $"Organize {(r?.Moves.Count ?? 0)}";
            if (Ui.PrimaryButton(label, buttonWidth))
            {
                if (handsFree) _ = Pilot!.RunOrganizerAsync(); else _ = organizer.RunAsync();
            }
        }
        if (r is { Report.Feasible: false }) Ui.Tooltip("A storage would overflow. Change a rule or free some space, then preview again.");
        else if (handsFree) Ui.Tooltip("Hands-free: opens the saddlebag and visits each retainer at an inn bell as the moves need.");
        else Ui.Tooltip("Moves what is shown. Storages that are closed keep their moves waiting until you open them.");
    }

    private void DrawRunning()
    {
        var pilotStatus = Pilot is { IsRunning: true } ? Pilot.Status : null;
        Ui.EmptyState(icons.Logo, "Organising…", pilotStatus ?? (organizer.LastProgress is { } p ? $"{p.Op.Info.Name}: {p.Message}" : null));
        Ui.Gap(0.5f);
        var frac = organizer.RunTotal == 0 ? 0f : (float)organizer.RunDone / organizer.RunTotal;
        ImGui.SetCursorPosX(ImGui.GetWindowWidth() * 0.2f);
        Ui.Progress(frac, ImGui.GetWindowWidth() * 0.6f, $"{organizer.RunDone} / {organizer.RunTotal}");
        Ui.Gap();
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - 120 * Ui.Scale) / 2);
        if (Ui.PrimaryButton("Stop", 120 * Ui.Scale, danger: true)) { Pilot?.Stop(); organizer.CancelRun(); }
        Ui.Centered("Stopping finishes the current move and leaves the rest untouched.", muted: true);
    }

    private string Name(StorageId s) => s.Kind switch
    {
        ContainerKind.Retainer => organizer.RetainerNames.TryGetValue(s.OwnerId, out var n) ? n : "a retainer",
        _ => s.Kind.DisplayName(),
    };
}
