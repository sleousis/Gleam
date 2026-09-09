using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using TidyUp.Core.Model;
using TidyUp.Core.Organizer.Capacity;
using TidyUp.Core.Organizer.Model;
using TidyUp.Core.Organizer.Execution;
using TidyUp.Core.Organizer.Solving;
using TidyUp.Game;
using TidyUp.Services;

namespace TidyUp.Windows;

/// <summary>
/// The organizer: a layout (ordered rules → destinations) on one side, the preview of what would move on
/// the other. Nothing moves until the player presses Organize on a preview that fits.
/// </summary>
/// <summary>The Organize half of the main window: layouts, rules, preview and the run screen. Drawn by <see cref="ConfirmationWindow"/>.</summary>
public sealed class OrganizerPanel
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
    private Guid? confirmRemove;
    private string note = string.Empty;
    private DateTime noteUntil;

    private void Note(string text) { note = text; noteUntil = DateTime.UtcNow.AddSeconds(5); }

    private static readonly IReadOnlyList<(View, string)> Views = [(View.Preview, "What will move"), (View.Rules, "Rules")];

    /// <summary>Set by the plugin when hands-free mode is available.</summary>
    public Automation.AutoPilot? Pilot { get; set; }

    /// <summary>Set by the host window: flips the header switch back to Clean.</summary>
    public Action? SwitchToClean { get; set; }

    /// <summary>Set by the host window: opens Settings in the same window.</summary>
    public Action? OpenSettings { get; set; }

    public OrganizerPanel(OrganizerCoordinator organizer, Configuration config, ItemDatabase db, IconCache icons, Action save)
    {
        this.organizer = organizer;
        this.config = config;
        this.db = db;
        this.icons = icons;
        this.save = save;
    }

    /// <summary>Called when the panel comes into view: makes sure there is a preview to show.</summary>
    public void OnShown()
    {
        if (organizer.Current is null && !organizer.IsPreviewing) _ = organizer.PreviewAsync();
    }

    private OrganizerPlan? Plan => config.Organizer.Active;
    private bool Simple => !config.AdvancedMode;

    /// <summary>
    /// The simple screen works on a layout of its own, made from the questions. Whatever layout was in use
    /// is remembered and handed back the moment advanced options come on again.
    /// </summary>
    private OrganizerPlan BorrowSimpleLayout()
    {
        var mine = config.Organizer.Plans.FirstOrDefault(p => p.Simple);
        if (mine is null)
        {
            mine = QuestionsLayout();
            mine.Simple = true;
            config.Organizer.Plans.Add(mine);
            dirty = true;
        }
        if (config.Organizer.ActivePlanId != mine.Id)
        {
            if (config.Organizer.Active is { Simple: false } theirs) config.Organizer.AdvancedPlanId = theirs.Id;
            config.Organizer.ActivePlanId = mine.Id;
            dirty = true;
        }
        return mine;
    }

    private void ReturnAdvancedLayout()
    {
        if (config.Organizer.Active is not { Simple: true }) return;
        var back = config.Organizer.Plans.FirstOrDefault(p => p.Id == config.Organizer.AdvancedPlanId && !p.Simple)
                   ?? config.Organizer.Plans.FirstOrDefault(p => !p.Simple);
        if (back is null) return;
        config.Organizer.ActivePlanId = back.Id;
        dirty = true;
    }

    public void Draw()
    {
        if (Simple) { DrawSimple(BorrowSimpleLayout()); return; }
        ReturnAdvancedLayout();
        var plan = Plan;

        var enabled = plan?.Rules.Count(r => r.Enabled) ?? 0;
        var subtitle = plan is null ? "No layout yet"
            : Rethinking ? $"{plan.Name} · working out what will move…"
            : $"{plan.Name} · {enabled} rule{(enabled == 1 ? "" : "s")}";
        Ui.Header(icons.LogoSmall, "Gleam", subtitle, Ui.SegmentedWidth(Views), () =>
        {
            if (Ui.Segmented("##view", ref view, Views) && view == View.Preview && organizer.Current is null && !Rethinking) _ = organizer.PreviewAsync();
        }, null, () => { if (Ui.ModeSwitch(Ui.AppMode.Organize)) SwitchToClean?.Invoke(); });

        DrawPlanBar();
        Ui.Gap(0.4f);
        if (!config.SeenOrganizeIntro && plan is not null)
        {
            if (Ui.Banner(Ui.Info, "New here?", "A layout is a short list of rules: what goes where. Start with the starter layout and change it later.", dismissLabel: "Got it"))
            {
                config.SeenOrganizeIntro = true;
                dirty = true;
            }
            Ui.Gap(0.4f);
        }

        if (Pilot is { IsRunning: true, Mode: Automation.PilotMode.Organize } || organizer.IsRunning) { DrawRunning(); return; }
        DrawBanners();

        var footer = ImGui.GetFrameHeight() * 2.4f + Ui.Space;
        using (var body = ImRaii.Child("##body", new Vector2(0, view == View.Preview ? -footer : 0), false, ImGuiWindowFlags.None))
        {
            if (body)
            {
                if (plan is null)
                {
                    Ui.EmptyState(icons.LogoMedium, "No layout yet.", "A layout says what goes where. The starter one is a sensible beginning.");
                    Ui.Gap(0.6f);
                    var w = 240 * Ui.Scale;
                    ImGui.SetCursorPosX((ImGui.GetWindowWidth() - w) / 2);
                    if (Ui.PrimaryButton("Use the starter layout", w))
                    {
                        var starter = OrganizerPlan.Starter();
                        config.Organizer.Plans.Add(starter);
                        config.Organizer.ActivePlanId = starter.Id;
                        view = View.Rules;
                        dirty = true;
                    }
                }
                else if (view == View.Rules) DrawRules(plan);
                else DrawPreview(plan);
            }
        }
        if (view == View.Preview && plan is not null) DrawFooter();

        SettleChanges();
    }

    private double previewDueAt;

    /// <summary>True while a change is waiting to be worked through, or is being worked through now.</summary>
    private bool Rethinking => previewDueAt > 0 || organizer.IsPreviewing;

    /// <summary>
    /// Any change to a layout makes what is on screen stale: a different layout, a rule added, a destination
    /// picked, an option toggled. The change is saved at once and the preview is redone a moment later, so a
    /// slider being dragged does not start a scan on every frame.
    /// </summary>
    private void SettleChanges()
    {
        if (dirty)
        {
            dirty = false;
            save();
            previewDueAt = ImGui.GetTime() + 0.35;
        }
        if (previewDueAt > 0 && ImGui.GetTime() >= previewDueAt && !organizer.IsPreviewing)
        {
            previewDueAt = 0;
            _ = organizer.PreviewAsync();
        }
    }

    private string? pilotErrorShown;
    private MoveRunReport? bannerReport;
    private bool bannerDismissed;

    /// <summary>Someone who came only to organize is offered the other half once, after their first run.</summary>
    private void DrawCleanOffer()
    {
        if (config.UseClean || !config.HasOrganizedOnce || config.AnsweredOrganizeOffer) return;
        if (Ui.Banner(Ui.Accent, "One more thing", "Gleam can also clear out junk. It shows you a list first and throws nothing away without your say-so.",
                dismissLabel: "No thanks", link: ("Show me", () =>
                {
                    config.UseClean = true;
                    config.AnsweredOrganizeOffer = true;
                    save();
                    SwitchToClean?.Invoke();
                })))
        {
            config.AnsweredOrganizeOffer = true;
            save();
        }
        Ui.Gap(0.3f);
    }

    /// <summary>One line after a run, and why the last hands-free run stopped, each dismissed with a click.</summary>
    private void DrawBanners()
    {
        var report = organizer.LastReport;
        if (report is not null)
        {
            if (!ReferenceEquals(report, bannerReport)) { bannerReport = report; bannerDismissed = false; }
            if (!bannerDismissed)
            {
                var parts = new List<string>();
                if (report.Done > 0) parts.Add($"moved {report.Done}");
                if (report.Skipped > 0) parts.Add($"skipped {report.Skipped} that changed");
                if (report.Failed > 0) parts.Add($"{report.Failed} failed");
                if (report.Pending.Count > 0) parts.Add($"{report.Pending.Count} waiting for their storage");
                if (parts.Count > 0 && Ui.Banner(report.Failed > 0 ? Ui.Warn : Ui.Ok, "Last run", string.Join(" · ", parts))) bannerDismissed = true;
            }
        }
        if (Pilot is { Mode: Automation.PilotMode.Organize, IsRunning: false, LastError: { } error } && pilotErrorShown != error)
        {
            if (Ui.Banner(Ui.Warn, "Hands-free stopped", error)) pilotErrorShown = error;
        }
    }

    // ---------- plan bar ----------

    private void DrawPlanBar()
    {
        // The simple screen's own layout is not one of the player's; it never appears in this list.
        var plans = config.Organizer.Plans.Where(p => !p.Simple).ToList();
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
            config.Organizer.Plans.Add(p);
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
                c.Simple = false;
                config.Organizer.Plans.Add(c);
                config.Organizer.ActivePlanId = c.Id;
                dirty = true;
            }
            ImGui.SameLine();
            bool deleteClicked;
            using (ImRaii.PushColor(ImGuiCol.Text, Ui.Danger, confirmDelete))
                deleteClicked = Ui.LinkButton(confirmDelete ? "Delete this layout?" : "Delete");
            Ui.Tooltip(confirmDelete ? "Click again to delete it for good." : "Deletes this layout. Asks once more first.");
            if (deleteClicked)
            {
                if (!confirmDelete) confirmDelete = true;
                else
                {
                    config.Organizer.Plans.Remove(active);
                    config.Organizer.ActivePlanId = config.Organizer.Plans.FirstOrDefault(p => !p.Simple)?.Id;
                    confirmDelete = false;
                    dirty = true;
                }
            }
            if (!ImGui.IsItemHovered() && confirmDelete && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) confirmDelete = false;
            ImGui.SameLine();
            if (Ui.LinkButton("Export"))
            {
                ImGui.SetClipboardText(OrganizerPlanCodec.Export(active));
                Note("Copied. Paste it anywhere to share this layout.");
            }
            Ui.Tooltip("Copies this layout as text you can share.");
        }
        ImGui.SameLine();
        if (Ui.LinkButton("Import"))
        {
            var imported = OrganizerPlanCodec.TryImport(ImGui.GetClipboardText(), organizer.RetainerNames.Keys.ToList());
            if (imported is null) Note("Nothing to import. Copy a Gleam layout first.");
            else
            {
                imported.Simple = false;
                config.Organizer.Plans.Add(imported);
                config.Organizer.ActivePlanId = imported.Id;
                view = View.Rules;
                dirty = true;
                Note($"Imported \"{imported.Name}\".");
            }
        }
        Ui.Tooltip("Adds a layout from text on your clipboard.");
        if (noteUntil > DateTime.UtcNow) { ImGui.SameLine(); Ui.Hint(note); }
        if (view == View.Preview && active is not null)
        {
            ImGui.SameLine();
            var w = ImGui.CalcTextSize("Look again", false, 0).X + ImGui.GetFrameHeight() + 20 * Ui.Scale;
            Ui.RightAlign(w);
            using (ImRaii.Disabled(organizer.IsPreviewing))
            {
                if (Ui.IconButton(FontAwesomeIcon.Sync, "Look again", w)) _ = organizer.PreviewAsync();
            }
            Ui.Tooltip("Looks through your storage again.");
        }
    }

    // ---------- rules ----------

    // ---------- the simple layer: quick setup, one sentence, one button ----------

    private void DrawSimple(OrganizerPlan plan)
    {
        var moves = organizer.Current?.Moves.Count ?? 0;
        var subtitle = Rethinking ? "Working out what will move…"
            : organizer.Current is null ? "Where things go"
            : moves == 0 ? "Everything is where you want it."
            : $"{moves} item{(moves == 1 ? "" : "s")} will move.";
        Ui.Header(icons.LogoSmall, "Gleam", subtitle, 0f, null, null,
            !config.UseClean ? null : () => { if (Ui.ModeSwitch(Ui.AppMode.Organize)) SwitchToClean?.Invoke(); });
        Ui.Gap(0.2f);
        using (ImRaii.Disabled(organizer.IsPreviewing))
        {
            if (Ui.IconButton(FontAwesomeIcon.Sync, "Look again")) _ = organizer.PreviewAsync();
        }
        Ui.Tooltip("Looks through your bags and storage again.");
        Ui.Gap(0.4f);

        if (Pilot is { IsRunning: true, Mode: Automation.PilotMode.Organize } || organizer.IsRunning) { DrawRunning(); return; }
        DrawBanners();
        DrawCleanOffer();

        var footer = ImGui.GetFrameHeight() * 2.4f + Ui.Space;
        using (var body = ImRaii.Child("##body", new Vector2(0, -footer), false, ImGuiWindowFlags.None))
        {
            if (body)
            {
                quickOpen = true;
                DrawQuickSetup(plan);
                Ui.Gap(0.6f);
                var result = organizer.Current;
                if (result is not null && !result.Report.Feasible)
                {
                    foreach (var s in result.Report.Shortfalls)
                        Ui.Banner(Ui.Danger, "Not enough room", $"{Name(s.Storage)} is {s.Short} slot{(s.Short == 1 ? "" : "s")} short. Free some space there or send fewer things to it.", dismissible: false);
                }
                else if (result is { Moves.Count: > 0 })
                {
                    var opens = result.StoragesToOpen.Select(Name).ToList();
                    Ui.Hint(opens.Count == 0 ? "Everything moves within your bags and armoury chest." : $"Gleam needs {string.Join(" and ", opens)} open. {(Pilot is not null && config.Automation.Enabled ? "It will go there for you." : "Open them and it carries on by itself.")}");
                }
                else if (result is not null) DrawNothingToMove(result);
            }
        }
        DrawFooter();

        SettleChanges();
    }

    /// <summary>
    /// Nothing will move, and the player deserves to know why. Usually everything is already in place; the
    /// interesting cases are storage Gleam has never looked inside, and things it deliberately left alone.
    /// </summary>
    private void DrawNothingToMove(SolveResult result)
    {
        var snapshot = organizer.Snapshot;
        var unseen = new List<string>();
        if (snapshot is not null)
        {
            foreach (var (id, name) in organizer.RetainerNames.OrderBy(kv => kv.Value))
                if (!snapshot.LiveContainers.Contains((ContainerKind.Retainer, id)) && !snapshot.Items.Any(i => i.Slot.Kind == ContainerKind.Retainer && i.Slot.OwnerId == id))
                    unseen.Add(name);
            if (!snapshot.LiveContainers.Any(c => c.Kind == ContainerKind.Saddlebag) && !snapshot.Items.Any(i => i.Slot.Kind == ContainerKind.Saddlebag))
                unseen.Add("your chocobo saddlebag");
        }

        using (Ui.Card("nothing"))
        {
            Ui.TextColored(Ui.Ok, "Everything is already where you asked for it.");
            Ui.HintWrapped("Gleam looked through your bags and armoury chest and found nothing that belongs somewhere else.");

            var left = result.Pinned.Count + result.NoRoom.Count;
            if (left > 0)
            {
                Ui.Gap(0.3f);
                Ui.Hint($"{left} item{(left == 1 ? "" : "s")} stayed put on purpose. Hover to see why.");
                if (ImGui.IsItemHovered())
                {
                    using var t = Ui.RichTooltip(360);
                    foreach (var g in result.Pinned.Concat(result.NoRoom).GroupBy(p => p.Reason).OrderByDescending(g => g.Count()).Take(8))
                        Ui.Text($"{g.Count()} × {g.Key}");
                }
            }

            if (unseen.Count > 0)
            {
                Ui.Gap(0.4f);
                Ui.HintWrapped($"Gleam has not seen inside {string.Join(", ", unseen)} yet. It looks when it goes there, or when you open one yourself.");
                if (Pilot?.MissingDependency() is { } missing)
                {
                    Ui.TextColored(Ui.Danger, $"Gleam cannot travel there: {missing}.");
                    if (Ui.LinkButton("What it needs")) OpenSettings?.Invoke();
                }
            }
        }
    }

    // ---------- quick setup: four questions that write the rules ----------

    private static readonly (string Name, string Question, OrganizerPredicate When, Destination Default)[] QuickQuestions =
    [
        ("Materia", "Materia", new OrganizerPredicate { Tags = [ItemTag.Materia] }, Destination.Saddlebag),
        ("Crystals", "Crystals and shards", new OrganizerPredicate { Tags = [ItemTag.Crystals] }, Destination.Saddlebag),
        ("Gear you are not using", "Gear that is not in a gear set", new OrganizerPredicate { Tags = [ItemTag.Gear], InGearset = false }, Destination.AnyRetainer),
        ("Gear in a gear set", "Gear that is in a gear set", new OrganizerPredicate { Tags = [ItemTag.Gear], InGearset = true }, Destination.Armoury),
        ("Housing items", "Housing items", new OrganizerPredicate { Tags = [ItemTag.Housing] }, Destination.AnyRetainer),
    ];

    /// <summary>A layout the questions own outright: one rule per question, in question order.</summary>
    private static OrganizerPlan QuestionsLayout()
    {
        var plan = new OrganizerPlan { Name = "Where things go" };
        foreach (var (name, _, when, dest) in QuickQuestions)
            plan.Rules.Add(new OrganizerRule { Name = name, When = Clone(when), Then = dest });
        return plan;
    }

    private bool quickOpen = true;

    /// <summary>Plain questions with a destination each. Changing one writes or removes the matching rule at once.</summary>
    private void DrawQuickSetup(OrganizerPlan plan)
    {
        using var card = Ui.Card("quick");
        Ui.TextColored(Ui.Muted, Simple ? "WHERE THINGS GO" : "QUICK SETUP");
        if (!Simple)
        {
            ImGui.SameLine();
            if (Ui.LinkButton(quickOpen ? "Hide" : "Show")) quickOpen = !quickOpen;
        }
        if (!quickOpen) return;
        Ui.Hint("Say where each kind of thing should live. Leave one alone and it stays where it is.");
        ImGui.Spacing();
        var labelW = QuickQuestions.Max(q => ImGui.CalcTextSize(q.Question, false, 0).X) + 12 * Ui.Scale;
        foreach (var (name, question, when, _) in QuickQuestions)
        {
            var rule = plan.Rules.FirstOrDefault(r => r.Name == name);
            var dest = rule?.Then ?? Destination.Stay;
            ImGui.AlignTextToFramePadding();
            Ui.Text(question);
            ImGui.SameLine(labelW);
            if (!DestinationCombo($"##quick{name}", ref dest, allowStay: true)) continue;
            if (dest.Kind == DestinationKind.Stay) { if (rule is not null) plan.Rules.Remove(rule); }
            else if (rule is null)
            {
                var order = QuickQuestions.Select(q => q.Name).ToList();
                var at = plan.Rules.Count(r => order.IndexOf(r.Name) is var i && i >= 0 && i < order.IndexOf(name));
                plan.Rules.Insert(Math.Min(at, plan.Rules.Count), new OrganizerRule { Name = name, When = Clone(when), Then = dest });
            }
            else rule.Then = dest;
            dirty = true;
        }
        if (!Simple) Ui.Hint("Everything else is listed below, where you can fine-tune or add your own rules.");
    }

    private static OrganizerPredicate Clone(OrganizerPredicate p) => new()
    {
        Tags = p.Tags is null ? null : new HashSet<ItemTag>(p.Tags), InGearset = p.InGearset, ForJobsPlayed = p.ForJobsPlayed,
        IsHq = p.IsHq, HasMateria = p.HasMateria, IsStackable = p.IsStackable, IsUntradable = p.IsUntradable, IsUnique = p.IsUnique,
        OnNeverTouchList = p.OnNeverTouchList, MinItemLevel = p.MinItemLevel, MaxItemLevel = p.MaxItemLevel, MinEquipLevel = p.MinEquipLevel, MaxEquipLevel = p.MaxEquipLevel,
    };

    private void DrawRules(OrganizerPlan plan)
    {
        DrawQuickSetup(plan);
        Ui.Gap(0.5f);
        using (Ui.Card("name"))
        {
            ImGui.AlignTextToFramePadding();
            Ui.Hint("Layout name");
            ImGui.SameLine();
            var name = plan.Name;
            ImGui.SetNextItemWidth(240 * Ui.Scale);
            if (Ui.InputText("##lname", "Layout name", ref name, 48)) { plan.Name = name; dirty = true; }
            ImGui.SameLine(0, 28 * Ui.Scale);
            Ui.Hint("Everything else goes to");
            ImGui.SameLine();
            var fallback = plan.Fallback;
            if (DestinationCombo("##fallback", ref fallback, allowStay: true)) { plan.Fallback = fallback; dirty = true; }
            Ui.Tooltip("Where items that match no rule go.");
        }

        Ui.Hint("Rules are tried from the top. The first one that matches decides.");
        Ui.Gap(0.3f);

        var toRemove = -1;
        for (var i = 0; i < plan.Rules.Count; i++)
        {
            var rule = plan.Rules[i];
            // Keyed by position: two rules that somehow share an id must never share ImGui state, or their
            // dropdowns draw into one popup.
            using var id = ImRaii.PushId(i);
            var selected = selectedRule == rule.Id;

            using (Ui.Card("rule"))
            {
                // Order badge, on/off, name, arrow, destination; the arrows and the bin sit at the right edge.
                ImGui.AlignTextToFramePadding();
                Ui.Pill($"{i + 1}", selected ? Ui.AccentSoft : Ui.Muted);
                ImGui.SameLine();
                var on = rule.Enabled;
                if (Ui.Check("##on", ref on)) { rule.Enabled = on; dirty = true; }
                Ui.Tooltip(on ? "This rule is on." : "This rule is off and skipped.");
                ImGui.SameLine();
                using (ImRaii.Disabled(!on))
                {
                    ImGui.SetNextItemWidth(190 * Ui.Scale);
                    var name = rule.Name;
                    if (Ui.InputText("##name", "Rule name", ref name, 48)) { rule.Name = name; dirty = true; }
                    ImGui.SameLine();
                    Ui.Icon(FontAwesomeIcon.ArrowRight, Ui.Muted);
                    ImGui.SameLine();
                    var dest = rule.Then;
                    if (DestinationCombo("##dest", ref dest, allowStay: true)) { rule.Then = dest; dirty = true; }
                    if (rule.Then.Kind is not (DestinationKind.Stay or DestinationKind.Bags))
                    {
                        ImGui.SameLine(0, 12 * Ui.Scale);
                        ImGui.AlignTextToFramePadding();
                        Ui.Hint("keep");
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(56 * Ui.Scale);
                        var keep = rule.KeepInBags;
                        using (ImRaii.PushColor(ImGuiCol.FrameBg, new Vector4(1, 1, 1, 0.06f)))
                        {
                            if (ImGui.InputInt("##keep", ref keep, 0, 0, "%d", ImGuiInputTextFlags.None)) { rule.KeepInBags = Math.Clamp(keep, 0, 9999); dirty = true; }
                        }
                        Ui.Tooltip("Keep up to this many in your bags and move only the rest. 0 moves everything. Whole stacks only, so one big stack stays.");
                        ImGui.SameLine();
                        Ui.Hint("in bags");
                    }
                }

                ImGui.SameLine();
                var glyphs = ImGui.GetFrameHeight() * 3 + ImGui.GetStyle().ItemSpacing.X * 2;
                Ui.RightAlign(glyphs);
                using (ImRaii.Disabled(i == 0))
                {
                    if (Ui.GlyphButton(FontAwesomeIcon.ArrowUp, "up", "Move up")) { (plan.Rules[i - 1], plan.Rules[i]) = (plan.Rules[i], plan.Rules[i - 1]); dirty = true; }
                }
                ImGui.SameLine();
                using (ImRaii.Disabled(i == plan.Rules.Count - 1))
                {
                    if (Ui.GlyphButton(FontAwesomeIcon.ArrowDown, "down", "Move down")) { (plan.Rules[i + 1], plan.Rules[i]) = (plan.Rules[i], plan.Rules[i + 1]); dirty = true; }
                }
                ImGui.SameLine();
                var armed = confirmRemove == rule.Id;
                if (Ui.GlyphButton(FontAwesomeIcon.Trash, "rm", armed ? "Click again to remove this rule." : "Remove this rule. Asks once more first.", armed ? Ui.Danger : null))
                {
                    if (armed) { toRemove = i; confirmRemove = null; }
                    else confirmRemove = rule.Id;
                }
                else if (armed && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.IsItemHovered()) confirmRemove = null;

                // Summary line doubles as the expand toggle.
                using (ImRaii.PushColor(ImGuiCol.Text, Ui.Muted))
                using (ImRaii.PushColor(ImGuiCol.HeaderHovered, new Vector4(1, 1, 1, 0.05f)))
                using (ImRaii.PushColor(ImGuiCol.HeaderActive, new Vector4(1, 1, 1, 0.08f)))
                {
                    var pos = ImGui.GetCursorScreenPos();
                    var chevronW = 16 * Ui.Scale;
                    if (ImGui.Selectable($"##sum", false, ImGuiSelectableFlags.None, new Vector2(0, ImGui.GetFrameHeight())))
                        selectedRule = selected ? null : rule.Id;
                    Ui.Tooltip(selected ? "Hide the conditions." : "Show and edit the conditions.");
                    var dl = ImGui.GetWindowDrawList();
                    var y = pos.Y + (ImGui.GetFrameHeight() - ImGui.GetTextLineHeight()) / 2;
                    var turn = Ui.Smooth($"chev:{i}", selected ? 1f : 0f, 14f);
                    using (ImRaii.PushFont(UiBuilder.IconFont))
                    {
                        dl.AddText(new Vector2(pos.X + 2 * Ui.Scale, y), ImGui.GetColorU32(Ui.Muted * new Vector4(1, 1, 1, 1f - turn)), FontAwesomeIcon.ChevronRight.ToIconString());
                        dl.AddText(new Vector2(pos.X + 2 * Ui.Scale, y), ImGui.GetColorU32(Ui.AccentSoft * new Vector4(1, 1, 1, turn)), FontAwesomeIcon.ChevronDown.ToIconString());
                    }
                    dl.AddText(new Vector2(pos.X + chevronW + 6 * Ui.Scale, y), ImGui.GetColorU32(on ? Ui.Muted : Ui.Muted * new Vector4(1, 1, 1, 0.6f)), $"{(when(rule).IsEmpty ? "Everything" : when(rule).Describe())} → {DestinationLabel(rule.Then)}{(rule.KeepInBags > 0 ? $", keep {rule.KeepInBags} in the bags" : "")}");
                }
                if (selected)
                {
                    using var fade = ImRaii.PushStyle(ImGuiStyleVar.Alpha, Ui.Appear($"pred:{rule.Id}", 0.2f));
                    DrawPredicateEditor(rule.When);
                }
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
        if (!ImGui.CollapsingHeader("More", ImGuiTreeNodeFlags.None)) return;
        using (Ui.Card("options"))
        {
            var merge = plan.MergeStacksAtDestination;
            if (Ui.Check("Top up stacks already at the destination", ref merge)) { plan.MergeStacksAtDestination = merge; dirty = true; }
            Ui.Tooltip("Off: incoming stacks take fresh slots instead.");
            var reserve = plan.BagStagingReserve;
            ImGui.SetNextItemWidth(100 * Ui.Scale);
            if (Ui.InputInt("Bag slots kept free while moving", ref reserve)) { plan.BagStagingReserve = Math.Clamp(reserve, 0, 100); dirty = true; }
            Ui.Tooltip("Items going from one retainer to another pass through your bags. This many bag slots stay free while they do.");

            var retainers = organizer.RetainerNames;
            if (retainers.Count > 0)
            {
                Ui.Gap(0.3f);
                Ui.Hint("Retainers this layout may use");
                var all = plan.RetainersInScope.Count == 0;
                if (Ui.Check("All of them", ref all)) { if (all) plan.RetainersInScope.Clear(); else plan.RetainersInScope.UnionWith(retainers.Keys); dirty = true; }
                if (!all)
                {
                    foreach (var (rid, rname) in retainers.OrderBy(kv => kv.Value))
                    {
                        var inScope = plan.RetainersInScope.Contains(rid);
                        if (Ui.Check($"{rname}##scope{rid}", ref inScope)) { if (inScope) plan.RetainersInScope.Add(rid); else plan.RetainersInScope.Remove(rid); dirty = true; }
                    }
                }
            }
        }
    }

    private void DrawPredicateEditor(OrganizerPredicate when)
    {
        Ui.Gap(0.3f);
        Ui.Hint("Matches items that fit all of these:");

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
        TriState("Quality", ref when, w => w.IsHq, (w, v) => w.IsHq = v, "High quality", "Normal quality");
        ImGui.SameLine(); TriState("Materia", ref when, w => w.HasMateria, (w, v) => w.HasMateria = v, "With materia", "Without materia");
        ImGui.SameLine(); TriState("Stacks", ref when, w => w.IsStackable, (w, v) => w.IsStackable = v, "Stackable", "Single");
        TriState("Trade", ref when, w => w.IsUntradable, (w, v) => w.IsUntradable = v, "Untradeable", "Tradeable");
        ImGui.SameLine(); TriState("Unique", ref when, w => w.IsUnique, (w, v) => w.IsUnique = v, "Unique", "Not unique");
        ImGui.SameLine(); TriState("Jobs", ref when, w => w.ForJobsPlayed, (w, v) => w.ForJobsPlayed = v, "Jobs I play", "Jobs I don't play");
        TriState("Gear set", ref when, w => w.InGearset, (w, v) => w.InGearset = v, "In a gear set", "Not in a gear set");
        ImGui.SameLine(); TriState("Keep these", ref when, w => w.OnNeverTouchList, (w, v) => w.OnNeverTouchList = v, "On the list", "Not on the list");

        // Levels
        Range("Item level", ref when, w => w.MinItemLevel, w => w.MaxItemLevel, (w, v) => w.MinItemLevel = v, (w, v) => w.MaxItemLevel = v, 999);
        Range("Equip level", ref when, w => w.MinEquipLevel, w => w.MaxEquipLevel, (w, v) => w.MinEquipLevel = v, (w, v) => w.MaxEquipLevel = v, 100);

        // Named items
        ImGui.AlignTextToFramePadding();
        Ui.Hint("Items");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220 * Ui.Scale);
        if (Ui.InputText("##itemsearch", "Add an item…", ref itemSearch, 64))
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
                if (ImGui.IsItemClicked() && when.ItemIds is not null) { when.ItemIds.Remove(id); if (when.ItemIds.Count == 0) when.ItemIds = null; dirty = true; }
                Ui.Tooltip("Click to remove.");
                ImGui.SameLine();
            }
            ImGui.NewLine();
        }
    }

    private void TriState(string label, ref OrganizerPredicate when, Func<OrganizerPredicate, bool?> get, Action<OrganizerPredicate, bool?> set, string yes, string no)
    {
        var options = new[] { "Any", yes, no };
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
        Ui.Tooltip("0 means no limit. Only gear has levels, so this never matches anything else.");
    }

    private static OrganizerPredicate when(OrganizerRule r) => r.When;

    private string DestinationLabel(Destination d) => d.Kind switch
    {
        DestinationKind.Stay => "stays put",
        DestinationKind.Bags => "bags",
        DestinationKind.Armoury => "armoury chest",
        DestinationKind.Saddlebag => "chocobo saddlebag",
        DestinationKind.Retainer when d.RetainerId == 0 => "any retainer",
        DestinationKind.Retainer => organizer.RetainerNames.TryGetValue(d.RetainerId, out var n) ? n : "a retainer",
        _ => d.Kind.ToString(),
    };

    private bool DestinationCombo(string id, ref Destination value, bool allowStay)
    {
        var options = new List<(Destination D, string Label)>();
        if (allowStay) options.Add((Destination.Stay, "Stays where it is"));
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
            Ui.EmptyState(icons.LogoMedium, organizer.IsPreviewing ? "Looking through your storage…" : "Nothing to show yet.", organizer.Status);
            if (!organizer.IsPreviewing && Ui.IconButton(FontAwesomeIcon.Sync, "Look again")) _ = organizer.PreviewAsync();
            return;
        }

        if (!result.Report.Feasible)
        {
            foreach (var s in result.Report.Shortfalls)
                Ui.Banner(Ui.Danger, "Not enough room", $"{Name(s.Storage)} is {s.Short} slot{(s.Short == 1 ? "" : "s")} short. Most of that comes from \"{s.TopRule}\".", dismissible: false);
            Ui.Gap(0.4f);
        }

        // End state cards
        var states = result.EndState.Where(e => e.UsedBefore != e.UsedAfter || result.Report.Shortfalls.Any(s => s.Storage == e.Storage)).ToList();
        if (states.Count > 0)
        {
            Ui.Hint("After organizing");
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
            Ui.EmptyState(icons.LogoMedium, "Everything is already where this layout wants it.", result.Pinned.Count + result.NoRoom.Count > 0 ? "Some items were left alone. See below." : null);
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
            if (!e.SizesAreLive) Ui.Tooltip("This storage has not been opened yet, so its size is assumed. Open it once for exact numbers.");
            var frac = Ui.Smooth($"state:{e.Storage}", e.Size == 0 ? 0f : Math.Clamp(e.UsedAfter / (float)e.Size, 0f, 1f), 8f);
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
        // Room for both pills side by side: the relay leg and the round.
        var legW = ImGui.CalcTextSize("To bags first", false, 0).X + ImGui.CalcTextSize("Round 10", false, 0).X + 56 * Ui.Scale;
        ImGui.TableSetupColumn("##leg", ImGuiTableColumnFlags.WidthFixed, legW, 0);

        foreach (var m in moves)
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.None, 28 * Ui.Scale);
            ImGui.TableNextColumn();
            Ui.ImageRounded(icons.Get(m.Info.IconId, m.Item.IsHq), new Vector2(24 * Ui.Scale, 24 * Ui.Scale), 4 * Ui.Scale);
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            Ui.Text(m.Info.Name);
            if (m.Item.Quantity > 1) { ImGui.SameLine(); Ui.Hint($"× {m.Item.Quantity}"); }
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            Ui.Hint($"{Name(m.From)}  →  {Name(m.To)}");
            ImGui.TableNextColumn();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 3 * Ui.Scale);
            if (m.Leg == MoveLeg.RelayOut) { Ui.Pill("To bags first", Ui.Info); Ui.Tooltip("Goes into your bags now and on to its destination when that storage is open."); }
            else if (m.Leg == MoveLeg.RelayIn) { Ui.Pill("From bags", Ui.Info); Ui.Tooltip("The second half of a move that passed through your bags."); }
            if (m.Pass > 1) { ImGui.SameLine(); Ui.Pill($"Round {m.Pass}", Ui.Warn); Ui.Tooltip("Needs room freed by earlier moves, so it runs in a later round."); }
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
            if (organizer.PendingMoves.Count > 0) parts.Add($"{organizer.PendingMoves.Count} from earlier still waiting");
        }
        ImGui.AlignTextToFramePadding();
        if (Simple) Ui.Hint(Rethinking ? "Working out what will move…" : r is null ? "" : r.Moves.Count == 0 ? "Nothing needs moving right now." : $"{r.Moves.Count} item{(r.Moves.Count == 1 ? "" : "s")} will move.");
        else Ui.Hint(parts.Count == 0 ? (string.IsNullOrEmpty(organizer.Status) ? "Refresh to see what would move." : organizer.Status) : string.Join("  ·  ", parts));

        var needsTravel = r is not null && r.StoragesToOpen.Any();
        var blocked = needsTravel ? Pilot?.MissingDependency() : null;
        var canRun = r is not null && r.Report.Feasible && r.Moves.Count > 0 && !Rethinking && blocked is null;
        var handsFree = Pilot is not null && config.Automation.Enabled && needsTravel;
        if (blocked is not null)
        {
            ImGui.SameLine();
            Ui.TextColored(Ui.Danger, $"Gleam cannot travel: {blocked}.");
        }
        var buttonWidth = 220 * Ui.Scale;
        var style = ImGui.GetStyle();
        var hereW = handsFree && !Simple ? ImGui.CalcTextSize("Organize here only", false, 0).X + style.FramePadding.X * 2 + style.ItemSpacing.X : 0;
        ImGui.SameLine();
        Ui.RightAlign(hereW + buttonWidth);
        if (handsFree && !Simple)
        {
            using (ImRaii.Disabled(!canRun))
            {
                if (Ui.LinkButton("Organize here only")) _ = organizer.RunAsync();
            }
            Ui.Tooltip("Moves what is reachable right now. The rest waits until you open its storage.");
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!canRun))
        {
            var n = r?.Moves.Count ?? 0;
            var items = $"{n} item{(n == 1 ? "" : "s")}";
            var label = r is { Report.Feasible: false } ? "Make room first"
                : n == 0 ? "Nothing to do"
                : handsFree && !Simple ? $"Organize {items} everywhere" : $"Organize {items}";
            if (Ui.PrimaryButton(label, buttonWidth))
            {
                if (handsFree) _ = Pilot!.RunOrganizerAsync(); else _ = organizer.RunAsync();
            }
        }
        if (r is { Report.Feasible: false }) Ui.Tooltip("A storage would overflow. Send fewer things there or free some space, then look again.");
        else if (handsFree) Ui.Tooltip(HandsFreeOrganizeHint);
        else Ui.Tooltip("Moves what is shown. Closed storages keep their moves waiting until you open them.");
    }

    private void DrawRunning()
    {
        var pilot = Pilot is { IsRunning: true } ? Pilot : null;
        var p = organizer.LastProgress;
        var current = p is null || !organizer.IsRunning ? null : $"{p.Op.Info.Name}{(p.Op.Item.Quantity > 1 ? $" × {p.Op.Item.Quantity}" : "")} · {p.Message}";
        Ui.RunningHeader(icons.LogoMedium, pilot is null ? "Organizing" : "Organizing hands-free", pilot?.Status ?? current);
        Ui.Gap(0.8f);

        var width = ImGui.GetWindowWidth() * 0.6f;
        var left = (ImGui.GetWindowWidth() - width) / 2;
        if (pilot is null)
        {
            // By hand: this batch is the whole story.
            ImGui.SetCursorPosX(left);
            Ui.ProgressBar("organize", organizer.RunTotal > 0 ? (float)organizer.RunDone / organizer.RunTotal : null, width, Ui.ProgressLabel(organizer.RunDone, organizer.RunTotal));
        }
        else
        {
            // Hands-free: the whole plan on top, the storage being worked on underneath.
            ImGui.SetCursorPosX(left);
            Ui.ProgressBar("organize", pilot.PlannedTotal > 0 ? (float)pilot.PlannedDone / pilot.PlannedTotal : null, width, Ui.ProgressLabel(pilot.PlannedDone, pilot.PlannedTotal), "Whole run");
            Ui.Gap(0.5f);
            ImGui.SetCursorPosX(left);
            var stopTotal = organizer.IsRunning ? organizer.RunTotal : 0;
            Ui.ProgressBar("organize-stop", stopTotal > 0 ? (float)organizer.RunDone / stopTotal : null, width,
                stopTotal > 0 ? Ui.ProgressLabel(organizer.RunDone, stopTotal) : null, organizer.IsRunning ? "At this stop" : "On the way", primary: false);
            if (current is not null) { Ui.Gap(0.3f); ImGui.SetCursorPosX(left); Ui.Hint(current); }
        }
        Ui.Gap(1.2f);
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - 120 * Ui.Scale) / 2);
        if (Ui.PrimaryButton("Stop", 120 * Ui.Scale, danger: true)) { Pilot?.Stop(); organizer.CancelRun(); }
        Ui.Gap(0.3f);
        Ui.Centered("Finishes the current move, then stops.", muted: true);
    }

    public const string HandsFreeOrganizeHint = "Hands-free: opens the saddlebag and visits each retainer at an inn bell as the moves need.";

    private string Name(StorageId s) => s.Kind switch
    {
        ContainerKind.Retainer => organizer.RetainerNames.TryGetValue(s.OwnerId, out var n) ? n : "a retainer",
        _ => s.Kind.DisplayName(),
    };
}
