using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Gleam.Core.Model;
using Gleam.Core.Rules;
using Gleam.Core.Stats;
using Gleam.Services;

namespace Gleam.Windows;

/// <summary>
/// "Your Gleam in numbers": what Gleam cleared, sold, listed and moved, and how much room and time that
/// gave back. Summary first, detail underneath. Quiet at rest; while a run is going the page follows it,
/// counting up as each item finishes.
/// </summary>
public sealed class StatsPage
{
    private static readonly IReadOnlyList<(StatsRange, string)> Ranges =
        [(StatsRange.Week, "7 days"), (StatsRange.Month, "30 days"), (StatsRange.Year, "1 year"), (StatsRange.AllTime, "All time")];

    private static readonly string[] Who = ["This character", "All characters"];
    private static readonly string[] ActivityNames = ["Discarded", "Sold", "Listed", "Turned in", "Desynthed", "Moved"];

    private readonly StatsService stats;
    private readonly RunCoordinator coordinator;
    private readonly OrganizerCoordinator organizer;
    private readonly Automation.AutoPilot pilot;
    private readonly Configuration config;
    private readonly IconCache icons;

    private readonly Dictionary<string, float> rowHeights = new();
    private readonly List<(string Tile, string Text, double At)> floaters = new();
    private readonly Dictionary<string, long> lastTargets = new();
    private (StatsRange, bool)? trackedFor;
    private double resetArmedAt = -10;

    /// <summary>Set by the host window.</summary>
    public Action? Back { get; set; }
    public Action? OpenSettings { get; set; }

    public StatsPage(StatsService stats, RunCoordinator coordinator, OrganizerCoordinator organizer, Automation.AutoPilot pilot, Configuration config, IconCache icons)
    {
        this.stats = stats;
        this.coordinator = coordinator;
        this.organizer = organizer;
        this.pilot = pilot;
        this.config = config;
        this.icons = icons;
    }

    public void OnShown()
    {
        stats.EnsureLoaded();
        stats.Invalidate();
    }

    // The action colours are the list's own, so a red bar here means what red means everywhere in Gleam.
    private static Vector4[] ActivityColors => [Ui.Danger, Ui.Ok, Ui.Market, Ui.Info, Ui.Warn, Ui.AccentSoft];

    private static (ContainerKind Kind, string Name, Vector4 Color)[] SourceKinds =>
    [
        (ContainerKind.Inventory, "Bags", Ui.AccentSoft),
        (ContainerKind.Armoury, "Armoury chest", Ui.Info),
        (ContainerKind.Retainer, "Retainers", Ui.Market),
        (ContainerKind.Saddlebag, "Saddlebag", Ui.Ok),
        (ContainerKind.GlamourDresser, "Dresser", Ui.Warn),
    ];

    private bool AnythingRunning => pilot.IsRunning || coordinator.IsRunning || organizer.IsRunning;

    public void Draw()
    {
        // The service only keeps the sums current while this page is being looked at.
        stats.MarkViewed();
        var snap = stats.Snapshot;
        var comboW = 140f * Ui.Scale;
        var rightW = Ui.SegmentedWidth(Ranges) + ImGui.GetStyle().ItemSpacing.X + comboW;
        const string title = "Your Gleam in numbers";
        var subtitle = Subtitle(snap);
        void Controls()
        {
            var range = stats.Range;
            if (Ui.Segmented("##statsRange", ref range, Ranges)) stats.Range = range;
            ImGui.SameLine();
            var who = stats.AllCharacters ? 1 : 0;
            ImGui.SetNextItemWidth(comboW);
            if (Ui.Combo("##statsWho", ref who, Who)) stats.AllCharacters = who == 1;
        }
        // The period and character pickers sit beside the title while both fit, and drop to a line of their
        // own in a narrow window rather than running over the title.
        var titleW = Math.Max(Charts.TextWidth(title), Charts.TextWidth(subtitle));
        var beside = ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X * 2 >= 46f * Ui.Scale + titleW + 16f * Ui.Scale + rightW;
        Ui.Header(icons.LogoSmall, title, subtitle, beside ? rightW : 0f, beside ? new Action(Controls) : null);
        if (!beside) Controls();
        if (Back is not null && Ui.BackLink()) Back();
        Ui.Gap(0.4f);

        if (snap is null)
        {
            if (stats.LoadFailed) Ui.EmptyState(icons.LogoMedium, "The numbers could not be read.", "Details are in the Dalamud log.");
            else Ui.RunningHeader(icons.LogoMedium, "Adding things up…", null);
            return;
        }
        if (snap.AllTimeHandled == 0 && snap.LastRun is null)
        {
            Ui.EmptyState(icons.LogoMedium, "Nothing to count yet.", "Every item Gleam cleans or puts away shows up here, and the charts fill in as it works.");
            return;
        }
        TrackChanges(snap);

        using var body = ImRaii.Child("##statsBody", Vector2.Zero, false, ImGuiWindowFlags.None);
        if (!body) return;
        var width = ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ScrollbarSize;
        var gap = 10f * Ui.Scale;

        DrawLive(snap, width);
        DrawTiles(snap, width, gap);
        if (width >= 820f * Ui.Scale)
        {
            Row("activity", width, gap, (0.62f, w => DrawActivity(snap, w)), (0.38f, w => DrawSources(snap, w)));
            Row("room", width, gap, (0.5f, w => DrawRoom(snap, w)), (0.5f, w => DrawRules(snap, w)));
            Row("trips", width, gap, (0.5f, w => DrawFlows(snap, w)), (0.5f, w => DrawTrip(snap, w)));
        }
        else
        {
            Row("activity", width, gap, (1f, w => DrawActivity(snap, w)));
            Row("sources", width, gap, (1f, w => DrawSources(snap, w)));
            Row("room", width, gap, (1f, w => DrawRoom(snap, w)));
            Row("rules", width, gap, (1f, w => DrawRules(snap, w)));
            Row("flows", width, gap, (1f, w => DrawFlows(snap, w)));
            Row("trip", width, gap, (1f, w => DrawTrip(snap, w)));
        }
        Row("year", width, gap, (1f, w => DrawYear(snap, w)));
        Row("milestones", width, gap, (1f, w => DrawMilestones(snap, w)));
        DrawFooter();
    }

    private string Subtitle(StatsSnapshot? snap)
    {
        var who = stats.AllCharacters ? "All characters" : "This character";
        if (config.StatsResetAt is { } reset) return $"{who} · counting since {reset.LocalDateTime:d MMM yyyy}";
        if (snap?.HistorySince is { } since) return $"{who} · since {since:d MMM yyyy}";
        return who;
    }

    /// <summary>A total that went up since the last snapshot gets a small "+n" rising off its tile.</summary>
    private void TrackChanges(StatsSnapshot snap)
    {
        var key = (snap.Query.Range, snap.Query.Character is null);
        var now = ImGui.GetTime();
        void Watch(string tile, long value)
        {
            if (trackedFor == key && lastTargets.TryGetValue(tile, out var before) && value > before && !Ui.Reduced)
                floaters.Add((tile, $"+{Charts.Short(value - before)}", now));
            lastTargets[tile] = value;
        }
        Watch("slots", snap.Current.SlotsFreed);
        Watch("gil", snap.Current.GilFromSales);
        Watch("listings", snap.Current.Listings);
        trackedFor = key;
        floaters.RemoveAll(f => now - f.At > 1.3);
    }

    // ---------- layout: rows of panels ----------

    /// <summary>
    /// Panels side by side, as tall as the tallest of them. Heights come from the last frame, so a row never
    /// jumps while it settles.
    /// </summary>
    private void Row(string id, float width, float gap, params (float Share, Action<float> Body)[] cells)
    {
        var start = ImGui.GetCursorScreenPos();
        var minH = rowHeights.GetValueOrDefault(id);
        var usable = width - gap * (cells.Length - 1);
        var x = start.X;
        var tallest = 0f;
        foreach (var (share, body) in cells)
        {
            var w = usable * share;
            ImGui.SetCursorScreenPos(new Vector2(x, start.Y));
            tallest = Math.Max(tallest, Panel($"{id}:{x:F0}", w, minH, body));
            x += w + gap;
        }
        rowHeights[id] = tallest;
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + Math.Max(tallest, minH) + gap));
        ImGui.Dummy(Vector2.Zero);
    }

    /// <summary>A softly raised panel of an exact width. Returns the height its content needed.</summary>
    private static float Panel(string id, float width, float minHeight, Action<float> body)
    {
        using var pid = ImRaii.PushId(id);
        var dl = ImGui.GetWindowDrawList();
        var pad = 12f * Ui.Scale;
        var start = ImGui.GetCursorScreenPos();
        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);
        ImGui.SetCursorScreenPos(start + new Vector2(pad, pad));
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(start.X - ImGui.GetWindowPos().X + width - pad);
        body(width - pad * 2);
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();
        var content = ImGui.GetItemRectMax().Y + pad - start.Y;
        var max = new Vector2(start.X + width, start.Y + Math.Max(content, minHeight));
        dl.ChannelsSetCurrent(0);
        var hover = Ui.Smooth($"stats:panel:{id}", ImGui.IsMouseHoveringRect(start, max, false) ? 1f : 0f, 14f);
        dl.AddRectFilled(start, max, Charts.Col(new Vector4(1f, 1f, 1f, 0.035f + 0.015f * hover)), 10f * Ui.Scale);
        dl.AddRect(start, max, Charts.Col(Ui.Mix(Ui.InkLine, Ui.InkEdge, hover)), 10f * Ui.Scale);
        dl.ChannelsMerge();
        return content;
    }

    /// <summary>A panel's title, with a quiet note to its right when there is room and underneath when not.</summary>
    private static void Title(string title, string? note, float width)
    {
        Ui.TextColored(Ui.AccentSoft, title);
        if (string.IsNullOrEmpty(note)) { Ui.Gap(0.2f); return; }
        var titleW = Charts.TextWidth(title);
        var noteW = Charts.TextWidth(note);
        if (titleW + noteW + 20f * Ui.Scale <= width)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + width - titleW - noteW - ImGui.GetStyle().ItemSpacing.X);
        }
        Ui.Hint(note);
        Ui.Gap(0.2f);
    }

    private static void Legend(IReadOnlyList<string> names, IReadOnlyList<Vector4> colors, float width)
    {
        var dl = ImGui.GetWindowDrawList();
        var square = 8f * Ui.Scale;
        var spacing = 14f * Ui.Scale;
        var used = 0f;
        for (var i = 0; i < names.Count; i++)
        {
            var w = square + 5f * Ui.Scale + Charts.TextWidth(names[i]);
            if (i > 0 && used + spacing + w <= width) { ImGui.SameLine(0, spacing); used += spacing + w; }
            else used = w;
            var p = ImGui.GetCursorScreenPos();
            var lh = ImGui.GetTextLineHeight();
            dl.AddRectFilled(p + new Vector2(0, (lh - square) / 2), p + new Vector2(square, (lh + square) / 2), Charts.Col(colors[i]), 2f * Ui.Scale);
            ImGui.Dummy(new Vector2(square + 5f * Ui.Scale, lh));
            ImGui.SameLine(0, 0);
            Ui.Hint(names[i]);
        }
    }

    // ---------- live strip ----------

    private (bool Running, string Title, string Status, int Done, int Total) LiveState()
    {
        if (pilot.IsRunning)
            return (true, pilot.Mode == Automation.PilotMode.Organize ? "Hands-free organize" : "Hands-free clean", pilot.Status, pilot.PlannedDone, pilot.PlannedTotal);
        if (coordinator.IsRunning) return (true, "Cleaning", $"{coordinator.RunDone} of {coordinator.RunTotal}", coordinator.RunDone, coordinator.RunTotal);
        if (organizer.IsRunning) return (true, "Putting things away", $"{organizer.RunDone} of {organizer.RunTotal}", organizer.RunDone, organizer.RunTotal);
        return (false, string.Empty, string.Empty, 0, 0);
    }

    /// <summary>While something runs: what, how far, and items a minute as a flowing line. At rest: the last run in one line.</summary>
    private void DrawLive(StatsSnapshot snap, float width)
    {
        var (running, title, status, done, total) = LiveState();
        if (!running && snap.LastRun is null) return;
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var h = 58f * Ui.Scale;
        ImGui.Dummy(new Vector2(width, h));
        var max = pos + new Vector2(width, h);
        dl.AddRectFilled(pos, max, Charts.Col(running ? Charts.Fade(Ui.Accent, 0.18f) : new Vector4(1, 1, 1, 0.035f)), 10f * Ui.Scale);
        dl.AddRect(pos, max, Charts.Col(running ? Charts.Fade(Ui.AccentSoft, 0.3f) : Ui.InkLine), 10f * Ui.Scale);

        var pad = 14f * Ui.Scale;
        var lineH = ImGui.GetTextLineHeight();
        var dot = pos + new Vector2(pad + 5f * Ui.Scale, h / 2);
        if (running && !Ui.Reduced)
        {
            var t = (float)(ImGui.GetTime() % 1.6 / 1.6);
            dl.AddCircle(dot, (5f + 9f * t) * Ui.Scale, Charts.Col(Charts.Fade(Ui.Ok, 0.6f * (1 - t))), 24, 2f * Ui.Scale);
        }
        dl.AddCircleFilled(dot, 5f * Ui.Scale, Charts.Col(running ? Ui.Ok : Ui.Muted));

        var tx = pos.X + pad + 20f * Ui.Scale;
        var rightW = Math.Min(220f * Ui.Scale, width * 0.3f);
        var textW = width - (tx - pos.X) - rightW - pad * 2;
        if (running)
        {
            Charts.Text(new Vector2(tx, pos.Y + 7f * Ui.Scale), Ui.Cream, title);
            Charts.Text(new Vector2(tx, pos.Y + 7f * Ui.Scale + lineH), Ui.Muted, Clip(status, textW));
            var barY = pos.Y + h - 12f * Ui.Scale;
            var fraction = Ui.Smooth("stats:liveBar", total > 0 ? Math.Clamp((float)done / total, 0f, 1f) : 0f, 8f);
            dl.AddRectFilled(new Vector2(tx, barY), new Vector2(tx + textW, barY + 5f * Ui.Scale), Charts.Col(new Vector4(1, 1, 1, 0.08f)), 3f * Ui.Scale);
            if (fraction > 0.001f)
                dl.AddRectFilled(new Vector2(tx, barY), new Vector2(tx + textW * fraction, barY + 5f * Ui.Scale), Charts.Col(Ui.AccentSoft), 3f * Ui.Scale);

            var sparkPos = new Vector2(max.X - pad - rightW, pos.Y + 8f * Ui.Scale);
            Charts.Sparkline("stats:rate", stats.Rate, sparkPos, new Vector2(rightW, h - 30f * Ui.Scale), Ui.AccentSoft, stats.RateScroll, ease: false);
            var rate = $"{stats.CurrentRate:0.#} items a minute";
            Charts.Text(new Vector2(max.X - pad - Charts.TextWidth(rate), max.Y - lineH - 4f * Ui.Scale), Ui.Muted, rate);
        }
        else
        {
            var last = snap.LastRun!;
            Charts.Text(new Vector2(tx, pos.Y + h / 2 - lineH), Ui.Cream, $"Last run {Ago(last.At)}");
            Charts.Text(new Vector2(tx, pos.Y + h / 2), Ui.Muted, Clip(Recap(last), width - (tx - pos.X) - pad));
        }
        Ui.Gap(0.5f);
    }

    private static string Recap(RunEvent run)
    {
        var how = run.Trigger switch
        {
            RunTrigger.HandsFree => "Hands-free clean",
            RunTrigger.HandsFreeOrganize => "Hands-free organize",
            RunTrigger.AfterVentures => "After ventures",
            RunTrigger.Organize or RunTrigger.StorageOpened => "Put away",
            _ => "Clean",
        };
        var parts = new List<string> { how, $"{run.Done:N0} item{(run.Done == 1 ? "" : "s")}", Duration((long)run.Seconds) };
        if (run.IsTrip) parts.Add($"{run.RetainersVisited} retainer{(run.RetainersVisited == 1 ? "" : "s")}, {run.Teleports} teleport{(run.Teleports == 1 ? "" : "s")}");
        if (run.Failed > 0) parts.Add($"{run.Failed} failed");
        if (run.Stopped) parts.Add("stopped early");
        return string.Join(" · ", parts);
    }

    // ---------- the four totals ----------

    private void DrawTiles(StatsSnapshot snap, float width, float gap)
    {
        var cols = width >= 760f * Ui.Scale ? 4 : 2;
        var tileW = (width - gap * (cols - 1)) / cols;
        var tileH = 94f * Ui.Scale;
        var start = ImGui.GetCursorScreenPos();
        var c = snap.Current;
        var p = snap.Previous;
        var seals = c.Seals > 0 ? $" Grand Company turn-ins brought {c.Seals:N0} seals as well." : string.Empty;
        var tiles = new (string Id, FontAwesomeIcon Icon, string Label, long Value, Func<long, string> Format, string? Note, Vector4 Color, List<float> Spark, string Tip)[]
        {
            ("slots", FontAwesomeIcon.BoxOpen, "Bag slots freed", c.SlotsFreed, v => $"{v:N0}", Change(c.SlotsFreed, p?.SlotsFreed), Ui.AccentSoft,
                snap.Activity.Select(b => (float)b.Slots).ToList(),
                "Each stack cleaned out of your bags, armoury chest, saddlebag or a retainer frees one slot." + (c.DresserFreed > 0 ? $" {c.DresserFreed:N0} more came out of the glamour dresser." : string.Empty)),
            ("gil", FontAwesomeIcon.Coins, "Gil from sales", c.GilFromSales, v => $"{v:N0}", Change(c.GilFromSales, p?.GilFromSales), Ui.Market,
                snap.Activity.Select(b => (float)b.Gil).ToList(),
                "Gil you actually received, from selling to a merchant or through a retainer." + seals),
            ("listings", FontAwesomeIcon.Store, "Listed on the market", c.Listings, v => $"{v:N0}", c.ListedValue > 0 ? $"asking {Charts.Short(c.ListedValue)} gil" : "asking price, not sales", Ui.AccentSoft,
                snap.Activity.Select(b => (float)b.Listings).ToList(),
                "Listings Gleam put up, and their total asking price. Whether they have sold is not something Gleam can see, so none of this is counted as earned."),
            ("time", FontAwesomeIcon.Clock, "Time saved, about", c.SecondsSaved, Duration, "an estimate", Ui.AccentSoft,
                snap.Activity.Select(b => (float)b.SecondsSaved).ToList(), TimeTip()),
        };
        for (var i = 0; i < tiles.Length; i++)
        {
            var t = tiles[i];
            var pos = start + new Vector2(i % cols * (tileW + gap), i / cols * (tileH + gap));
            DrawTile(t.Id, pos, new Vector2(tileW, tileH), t.Icon, t.Label, t.Value, t.Format, t.Note, t.Color, t.Spark, t.Tip);
        }
        var rows = (tiles.Length + cols - 1) / cols;
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(width, rows * tileH + (rows - 1) * gap));
        Ui.Gap(0.5f);
    }

    private void DrawTile(string id, Vector2 pos, Vector2 size, FontAwesomeIcon icon, string label, long value, Func<long, string> format, string? note, Vector4 color, List<float> spark, string tip)
    {
        var dl = ImGui.GetWindowDrawList();
        var max = pos + size;
        var hovered = Charts.Hovered(pos, size);
        var lift = Ui.Smooth($"stats:tile:{id}", hovered ? 1f : 0f, 14f);
        dl.AddRectFilled(pos, max, Charts.Col(new Vector4(1, 1, 1, 0.035f + 0.02f * lift)), 10f * Ui.Scale);
        dl.AddRect(pos, max, Charts.Col(Ui.Mix(Ui.InkLine, Ui.InkEdge, lift)), 10f * Ui.Scale);

        var pad = 12f * Ui.Scale;
        var lineH = ImGui.GetTextLineHeight();
        var y = pos.Y + pad;
        using (ImRaii.PushFont(UiBuilder.IconFont)) Charts.Text(new Vector2(pos.X + pad, y + 1f * Ui.Scale), Ui.Muted, icon.ToIconString());
        Charts.Text(new Vector2(pos.X + pad + 20f * Ui.Scale, y), Ui.Muted, label);
        y += lineH + 3f * Ui.Scale;
        // The number keeps to the left of the sparkline: a long gil total in a narrow tile is drawn a little
        // smaller rather than running into the line beside it.
        const float big = 1.75f;
        var number = format(Ui.Count($"stats:{id}", value));
        var room = size.X * 0.58f - pad * 1.5f;
        var scale = Math.Clamp(room / Math.Max(1f, Charts.TextWidth(number)), 1.15f, big);
        Charts.BigText(new Vector2(pos.X + pad, y + ImGui.GetFontSize() * (big - scale) / 2), color, number, scale);
        y += ImGui.GetFontSize() * big + 3f * Ui.Scale;
        if (note is not null) Charts.Text(new Vector2(pos.X + pad, y), note.StartsWith('+') ? Ui.Ok : Ui.Muted, Clip(note, size.X * 0.55f));

        Charts.Sparkline($"stats:spark:{id}", spark, new Vector2(pos.X + size.X * 0.58f, max.Y - pad - 24f * Ui.Scale), new Vector2(size.X * 0.42f - pad, 24f * Ui.Scale), color);

        var now = ImGui.GetTime();
        foreach (var f in floaters.Where(f => f.Tile == id))
        {
            var age = (float)(now - f.At);
            var alpha = age < 0.2f ? age / 0.2f : Math.Max(0f, 1f - (age - 0.2f) / 1.0f);
            var fy = pos.Y + pad + 4f * Ui.Scale - age * 16f * Ui.Scale;
            Charts.Text(new Vector2(max.X - pad - Charts.TextWidth(f.Text), fy), Charts.Fade(color, alpha), f.Text);
        }

        if (hovered)
        {
            using (Ui.RichTooltip(300f)) ImGui.TextWrapped(tip);
        }
    }

    private string? Change(long now, long? before)
    {
        if (before is not { } b || b <= 0) return null;
        var pct = (now - b) * 100.0 / b;
        var span = stats.Range switch { StatsRange.Week => "7 days", StatsRange.Month => "30 days", _ => "year" };
        return $"{(pct >= 0 ? "+" : "")}{pct:0}% on the {span} before";
    }

    private static string TimeTip() =>
        $"About {StatsEngine.Cost.Discard}s for each discard, {StatsEngine.Cost.Sell}s a sale, {StatsEngine.Cost.List}s a listing, " +
        $"{StatsEngine.Cost.TurnIn}s a turn-in, {StatsEngine.Cost.Desynth}s a desynth, {StatsEngine.Cost.MoveLeg}s a move ({StatsEngine.Cost.MoveLeg * 2}s through the bags), " +
        $"and {StatsEngine.Cost.RetainerVisit}s for every retainer visited. An estimate of the clicking Gleam did for you, not a stopwatch.";

    // ---------- day by day ----------

    private void DrawActivity(StatsSnapshot snap, float w)
    {
        Title("What Gleam did, day by day", snap.BucketDays switch { 1 => $"last {snap.Activity.Count} days", 7 => "a bar per week", _ => "a bar per month" }, w);
        Legend(ActivityNames, ActivityColors, w);
        Ui.Gap(0.3f);
        var pos = ImGui.GetCursorScreenPos();
        var size = new Vector2(w, 190f * Ui.Scale);
        ImGui.Dummy(size);
        var stacks = snap.Activity.Select(b => b.Counts.Select(n => (float)n).ToArray()).ToList();
        var hover = Charts.StackedBars("stats:activity", stacks, ActivityColors, pos, size, i => BarLabel(snap, i));
        if (hover < 0) return;
        var bucket = snap.Activity[hover];
        using (Ui.RichTooltip(230f))
        {
            Ui.Hint(BucketName(snap, hover));
            if (bucket.Handled == 0) { Ui.Text("Nothing that day."); return; }
            for (var j = 0; j < ActivityNames.Length; j++)
            {
                if (bucket.Counts[j] == 0) continue;
                Ui.TextColored(ActivityColors[j], ActivityNames[j]);
                ImGui.SameLine(150f * Ui.Scale);
                Ui.Text($"{bucket.Counts[j]:N0}");
            }
            if (bucket.Gil > 0) Ui.TextColored(Ui.Market, $"{bucket.Gil:N0} gil from sales");
        }
    }

    private static string? BarLabel(StatsSnapshot snap, int i)
    {
        var b = snap.Activity[i];
        if (i == snap.Activity.Count - 1 && snap.BucketDays == 1) return "today";
        return snap.BucketDays > 7 ? b.Start.ToString("MMM yy") : b.Start.ToString("d MMM");
    }

    private static string BucketName(StatsSnapshot snap, int i)
    {
        var b = snap.Activity[i];
        if (snap.BucketDays == 1) return b.Start.ToString("dddd d MMMM") + (i == snap.Activity.Count - 1 ? " · today" : string.Empty);
        return $"{b.Start:d MMM} to {b.Start.AddDays(b.Days - 1):d MMM}";
    }

    // ---------- where it came from ----------

    private static void DrawSources(StatsSnapshot snap, float w)
    {
        Title("Where the junk came from", "stacks cleaned", w);
        var kinds = SourceKinds;
        var slices = kinds.Select(k => ((float)snap.Sources.GetValueOrDefault(k.Kind), k.Color)).ToList();
        var total = slices.Sum(x => x.Item1);
        if (total <= 0) { Ui.HintWrapped("Nothing was cleaned in this period."); return; }

        var radius = 56f * Ui.Scale;
        var thickness = 13f * Ui.Scale;
        var box = 2 * (radius + thickness);
        var pos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(box, box));
        var hover = Charts.Donut("stats:sources", slices, pos + new Vector2(box / 2), radius, thickness, Charts.Short((long)total), "stacks");

        var beside = w - box > 150f * Ui.Scale;
        var keysX = beside ? pos.X + box + 16f * Ui.Scale : pos.X;
        var keysW = beside ? w - box - 16f * Ui.Scale : w;
        var lineH = ImGui.GetTextLineHeight() + 5f * Ui.Scale;
        var y = beside ? pos.Y + (box - lineH * kinds.Length) / 2 : ImGui.GetCursorScreenPos().Y + 4f * Ui.Scale;
        var dl = ImGui.GetWindowDrawList();
        for (var i = 0; i < kinds.Length; i++)
        {
            var count = snap.Sources.GetValueOrDefault(kinds[i].Kind);
            var row = new Vector2(keysX, y + i * lineH);
            var sq = 9f * Ui.Scale;
            var lh = ImGui.GetTextLineHeight();
            dl.AddRectFilled(row + new Vector2(0, (lh - sq) / 2), row + new Vector2(sq, (lh + sq) / 2), Charts.Col(kinds[i].Color), 2f * Ui.Scale);
            Charts.Text(row + new Vector2(sq + 7f * Ui.Scale, 0), hover == i ? Ui.Cream : Charts.Fade(Ui.Cream, 0.85f), kinds[i].Name);
            var n = $"{count:N0}";
            Charts.Text(new Vector2(keysX + keysW - Charts.TextWidth(n), row.Y), Ui.Muted, n);
        }
        if (!beside) ImGui.Dummy(new Vector2(w, lineH * kinds.Length + 4f * Ui.Scale));

        if (hover < 0) return;
        var share = snap.Sources.GetValueOrDefault(kinds[hover].Kind) / total;
        using (Ui.RichTooltip(200f))
        {
            Ui.TextColored(kinds[hover].Color, kinds[hover].Name);
            Ui.Text($"{snap.Sources.GetValueOrDefault(kinds[hover].Kind):N0} stacks, {share * 100:0}% of the period's");
        }
    }

    // ---------- room in the bags ----------

    private static void DrawRoom(StatsSnapshot snap, float w)
    {
        Title("Room in your bags", "used slots · dots are Gleam runs", w);
        if (snap.Room.Count < 2)
        {
            Ui.HintWrapped("Gleam notes how full your bags are every time it looks through them, from this version on. The chart fills in after a few looks.");
            return;
        }
        var fromLocal = snap.From.ToDateTime(TimeOnly.MinValue);
        var from = new DateTimeOffset(fromLocal, TimeZoneInfo.Local.GetUtcOffset(fromLocal));
        var span = Math.Max(1.0, (DateTimeOffset.Now - from).TotalSeconds);
        float X(DateTimeOffset at) => (float)Math.Clamp((at - from).TotalSeconds / span, 0, 1);

        var bags = snap.Room.Select(p => new Vector2(X(p.At), p.BagsUsed)).ToList();
        var saddle = snap.Room.Where(p => p.SaddleUsed is not null).Select(p => new Vector2(X(p.At), p.SaddleUsed!.Value)).ToList();
        var bagCap = snap.Room[^1].BagsCapacity;
        var saddleCap = snap.Room.LastOrDefault(p => p.SaddleUsed is not null)?.SaddleCapacity ?? 70;
        var series = new List<Charts.Series> { new(bags, Ui.AccentSoft, false, true) };
        if (saddle.Count >= 2) series.Add(new Charts.Series(saddle, Ui.Info, true, false));

        var pos = ImGui.GetCursorScreenPos();
        var size = new Vector2(w, 150f * Ui.Scale);
        ImGui.Dummy(size);
        var hover = Charts.Lines("stats:room", series, Math.Max(bagCap, saddleCap), pos, size, snap.RunMarks.Select(X).ToList());
        Legend(saddle.Count >= 2 ? [$"Bags, of {bagCap}", $"Saddlebag, of {saddleCap}"] : [$"Bags, of {bagCap}"], [Ui.AccentSoft, Ui.Info], w);

        if (hover < 0) return;
        var point = snap.Room[hover];
        using (Ui.RichTooltip(220f))
        {
            Ui.Hint(point.At.LocalDateTime.ToString("ddd d MMM, HH:mm"));
            Ui.Text($"Bags: {point.BagsUsed} of {point.BagsCapacity} slots used");
            if (point.SaddleUsed is { } s) Ui.Text($"Saddlebag: {s} of {point.SaddleCapacity}");
        }
    }

    // ---------- rules ----------

    private void DrawRules(StatsSnapshot snap, float w)
    {
        Title("Rules at work", "suggested · kept ticked", w);
        var lines = snap.Rules.Take(6).ToList();
        if (lines.Count == 0)
        {
            Ui.HintWrapped("Which rules found your junk, and how often you kept their picks. It fills in as you clean.");
            return;
        }
        var decisions = lines.Any(l => l.Suggested > 0);
        var top = Math.Max(1, lines.Max(l => decisions ? l.Suggested : l.Cleaned));
        var dl = ImGui.GetWindowDrawList();
        var flagged = false;
        foreach (var line in lines)
        {
            var often = decisions && line.Suggested >= 10 && line.KeptShare < 0.5;
            flagged |= often;
            var rowX = ImGui.GetCursorPosX();
            Ui.Text(RuleName(line.RuleId));
            var right = decisions && line.Suggested > 0 ? $"{line.Suggested:N0} · {line.KeptShare * 100:0}%" : $"{line.Cleaned:N0} cleaned";
            ImGui.SameLine();
            ImGui.SetCursorPosX(rowX + w - Charts.TextWidth(right));
            Ui.Hint(right);

            var p = ImGui.GetCursorScreenPos();
            var barH = 7f * Ui.Scale;
            ImGui.Dummy(new Vector2(w, barH));
            var keep = decisions ? line.Kept : line.Cleaned;
            var drop = decisions ? line.Suggested - line.Kept : 0;
            var kw = Charts.Grow("stats:rules", line.RuleId.GetHashCode(), (float)keep / top) * w;
            var dw = Charts.Grow("stats:rules", line.RuleId.GetHashCode() ^ 0x5A5A, (float)drop / top) * w;
            dl.AddRectFilled(p, p + new Vector2(w, barH), Charts.Col(new Vector4(1, 1, 1, 0.06f)), 4f * Ui.Scale);
            if (kw > 1f) dl.AddRectFilled(p, p + new Vector2(kw, barH), Charts.Col(Ui.AccentSoft), 4f * Ui.Scale);
            if (dw > 1f) dl.AddRectFilled(p + new Vector2(kw, 0), p + new Vector2(kw + dw, barH), Charts.Col(Charts.Fade(Ui.Warn, 0.55f)), 4f * Ui.Scale);
            // Under the bar, not beside the name: beside it, a long rule name ran into its own figures.
            if (often) Ui.TextColored(Ui.Warn, "You untick this one more often than you keep it.");
            Ui.Gap(0.3f);
        }
        if (flagged && OpenSettings is not null && Ui.LinkButton("Adjust a rule in Settings")) OpenSettings();
    }

    private static string RuleName(string id) =>
        RuleEngine.AllRules.FirstOrDefault(r => r.Id == id)?.Name ?? id switch
        {
            "vendor-vs-market" => "Worth more on the market",
            _ => char.ToUpperInvariant(id.FirstOrDefault()) + id.Replace('-', ' ').Skip(1).Aggregate(string.Empty, (a, ch) => a + ch),
        };

    // ---------- the organizer's ribbons ----------

    private void DrawFlows(StatsSnapshot snap, float w)
    {
        Title("Where the organizer put things", $"{snap.Current.Moved:N0} put away", w);
        if (snap.Flows.Count == 0)
        {
            Ui.HintWrapped("Nothing was put away in this period.");
            return;
        }
        var names = organizer.RetainerNames;
        string Node(ContainerKind kind, ulong owner) => kind switch
        {
            ContainerKind.Retainer => names.TryGetValue(owner, out var n) ? n : "A retainer",
            ContainerKind.Inventory => "Bags",
            ContainerKind.Armoury => "Armoury",
            ContainerKind.Saddlebag => "Saddlebag",
            ContainerKind.GlamourDresser => "Dresser",
            _ => kind.ToString(),
        };
        var ribbons = snap.Flows.Take(8).Select(f => new Charts.Ribbon(Node(f.FromKind, f.FromOwner), Node(f.ToKind, f.ToOwner), f.Count)).ToList();
        var pos = ImGui.GetCursorScreenPos();
        var size = new Vector2(w, Math.Max(120f, 34f * Math.Max(ribbons.Select(r => r.To).Distinct().Count(), ribbons.Select(r => r.From).Distinct().Count())) * Ui.Scale);
        ImGui.Dummy(size);
        var live = organizer.IsRunning || pilot is { IsRunning: true, Mode: Automation.PilotMode.Organize };
        var hover = Charts.Ribbons("stats:flows", ribbons, pos, size, live);
        if (hover < 0) return;
        using (Ui.RichTooltip(220f)) Ui.Text($"{ribbons[hover].From} to {ribbons[hover].To}: {ribbons[hover].Count:N0}");
    }

    // ---------- the last trip ----------

    private void DrawTrip(StatsSnapshot snap, float w)
    {
        var trip = snap.LastTrip;
        Title("The last hands-free trip", trip is null ? null : $"{Duration((long)trip.Seconds)} · {Ago(trip.At)}", w);
        if (pilot.IsRunning) Ui.TextColored(Ui.Ok, Clip($"A trip is under way: {pilot.Status}", w));
        if (trip is null)
        {
            Ui.HintWrapped("Your next hands-free run shows up here: every stop, how long each took, and how far Gleam walked for you.");
        }
        else
        {
            var legs = MergeLegs(trip.Legs);
            var pos = ImGui.GetCursorScreenPos();
            var size = new Vector2(w, 28f * Ui.Scale);
            ImGui.Dummy(size);
            var hover = Charts.Segments("stats:legs", legs.Select(l => (l.Label, (float)l.Seconds, LegColor(l.Label))).ToList(), pos, size);
            if (hover >= 0)
                using (Ui.RichTooltip(220f))
                    Ui.Text($"{legs[hover].Label}: {Duration((long)legs[hover].Seconds)}{(legs[hover].Ok ? string.Empty : ", did not finish")}");
            Ui.Gap(0.2f);
            Ui.Hint($"{trip.Teleports} teleport{(trip.Teleports == 1 ? "" : "s")} · {trip.WalkedYalms:N0} yalms walked for you · {trip.RetainersVisited} retainer{(trip.RetainersVisited == 1 ? "" : "s")}");
        }

        Ui.Gap(0.5f);
        if (snap.SuccessRate is not { } rate)
        {
            Ui.Hint("How often each action goes through is counted from your next run on.");
            return;
        }
        var ringPos = ImGui.GetCursorScreenPos();
        var r = 26f * Ui.Scale;
        ImGui.Dummy(new Vector2(r * 2 + 8f * Ui.Scale, r * 2 + 8f * Ui.Scale));
        Charts.Ring("stats:success", (float)rate, ringPos + new Vector2(r + 4f * Ui.Scale), r, 6f * Ui.Scale, Ui.Ok, Charts.Fade(Ui.Danger, 0.35f), $"{rate * 100:0.#}%");
        ImGui.SameLine();
        ImGui.BeginGroup();
        var failed = snap.Failed == 0
            ? $"All {snap.Done:N0} actions in this period went through."
            : $"{snap.Done:N0} of {snap.Done + snap.Failed:N0} actions in this period went through." + (snap.TopFailure is { } why ? $" The most common reason for the rest: {why}." : string.Empty);
        ImGui.TextWrapped(failed);
        ImGui.EndGroup();
    }

    private static List<(string Label, double Seconds, bool Ok)> MergeLegs(IEnumerable<LegRecord> legs)
    {
        var merged = new List<(string Label, double Seconds, bool Ok, int Count)>();
        foreach (var leg in legs)
        {
            if (merged.Count > 0 && merged[^1].Label == leg.Name)
            {
                var last = merged[^1];
                merged[^1] = (last.Label, last.Seconds + leg.Seconds, last.Ok && leg.Ok, last.Count + 1);
            }
            else merged.Add((leg.Name, leg.Seconds, leg.Ok, 1));
        }
        return merged.Select(m => (m.Label == "Retainer" ? (m.Count == 1 ? "Retainer" : $"Retainers ×{m.Count}") : m.Label, m.Seconds, m.Ok)).ToList();
    }

    private static Vector4 LegColor(string label) => label switch
    {
        "Bags" or "Items brought back" => Ui.AccentSoft,
        "Saddlebag" => Ui.Ok,
        "Travel to the inn" => Ui.Muted,
        "Dresser" => Ui.Warn,
        "Merchant" or "Grand Company" => Ui.Info,
        _ when label.StartsWith("Retainer", StringComparison.Ordinal) => Ui.Market,
        _ => Ui.Muted,
    };

    // ---------- the year ----------

    private void DrawYear(StatsSnapshot snap, float w)
    {
        Title("Your year with Gleam", snap.LongestStreak > 1 ? $"longest streak {snap.LongestStreak} days · now {snap.CurrentStreak}" : "a square per day · brighter means busier", w);
        var gap = 3f * Ui.Scale;
        var columns = (snap.Year.Count + 6) / 7;
        var cell = Math.Clamp((w - gap * (columns - 1)) / columns, 5f * Ui.Scale, 13f * Ui.Scale);
        var size = Charts.HeatmapSize(snap.Year.Count, cell, gap);
        var pos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);
        var hover = Charts.Heatmap("stats:year", snap.Year, pos, cell, gap, AnythingRunning);
        if (hover < 0) return;
        var day = snap.YearStart.AddDays(hover);
        using (Ui.RichTooltip(200f)) Ui.Text($"{day:ddd d MMM yyyy}: {snap.Year[hover]:N0} handled");
    }

    // ---------- milestones ----------

    private void DrawMilestones(StatsSnapshot snap, float w)
    {
        Title("Milestones", "all characters, all time", w);
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var h = ImGui.GetFrameHeight();
        var lineH = ImGui.GetTextLineHeight();
        var spacing = 8f * Ui.Scale;
        string star;
        float starW;
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            star = FontAwesomeIcon.Star.ToIconString();
            starW = Charts.TextWidth(star);
        }
        var x = origin.X;
        var y = origin.Y;
        var now = DateTime.UtcNow;
        foreach (var m in snap.Milestones)
        {
            var label = m.Earned ? m.Title : $"{m.Title}  {Charts.Short(m.Progress)} / {Charts.Short(m.Target)}";
            var bw = 12f * Ui.Scale + starW + 7f * Ui.Scale + Charts.TextWidth(label) + 12f * Ui.Scale;
            if (x + bw > origin.X + w && x > origin.X) { x = origin.X; y += h + spacing; }
            var min = new Vector2(x, y);
            var max = min + new Vector2(bw, h);
            dl.AddRectFilled(min, max, Charts.Col(m.Earned ? Charts.Fade(Ui.Accent, 0.22f) : new Vector4(1, 1, 1, 0.03f)), h / 2);
            dl.AddRect(min, max, Charts.Col(m.Earned ? Charts.Fade(Ui.AccentSoft, 0.45f) : Ui.InkLine), h / 2);
            if (stats.FreshMilestones.TryGetValue(m.Id, out var at) && (now - at).TotalSeconds < 8 && !Ui.Reduced)
            {
                // A new one catches the light a few times, then settles.
                var t = (float)((now - at).TotalSeconds * 0.9 % 1.0);
                var band = 30f * Ui.Scale;
                var cx = min.X - band + (bw + band * 2) * t;
                var clear = Charts.Col(Charts.Fade(Ui.Market, 0f));
                var shine = Charts.Col(Charts.Fade(Ui.Market, 0.5f));
                ImGui.PushClipRect(min, max, true);
                dl.AddRectFilledMultiColor(new Vector2(cx - band, min.Y), new Vector2(cx, max.Y), clear, shine, shine, clear);
                dl.AddRectFilledMultiColor(new Vector2(cx, min.Y), new Vector2(cx + band, max.Y), shine, clear, clear, shine);
                ImGui.PopClipRect();
            }
            using (ImRaii.PushFont(UiBuilder.IconFont))
                Charts.Text(new Vector2(min.X + 12f * Ui.Scale, min.Y + (h - ImGui.GetTextLineHeight()) / 2), m.Earned ? Ui.Market : Ui.Muted, star);
            Charts.Text(new Vector2(min.X + 12f * Ui.Scale + starW + 7f * Ui.Scale, min.Y + (h - lineH) / 2), m.Earned ? Ui.Cream : Ui.Muted, label);
            x += bw + spacing;
        }
        ImGui.Dummy(new Vector2(w, y + h - origin.Y));
    }

    // ---------- footer ----------

    private void DrawFooter()
    {
        Ui.Hint("These numbers stay on this PC. Gleam sends nothing anywhere.");
        var armed = ImGui.GetTime() - resetArmedAt < 4;
        var label = armed ? "Really reset? Click again" : "Reset statistics";
        ImGui.SameLine();
        Ui.RightAlign(Charts.TextWidth(label) + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetStyle().ScrollbarSize);
        if (Ui.LinkButton(label))
        {
            if (armed) { resetArmedAt = -10; _ = stats.ResetAsync(); }
            else resetArmedAt = ImGui.GetTime();
        }
        Ui.Tooltip("Counting starts again from now. The history of what Gleam did stays as it is.");
        Ui.Gap(0.5f);
    }

    // ---------- words ----------

    private static string Duration(long seconds)
    {
        if (seconds < 60) return $"{seconds}s";
        if (seconds < 3600) return $"{seconds / 60}m {seconds % 60:00}s";
        return $"{seconds / 3600}h {seconds % 3600 / 60:00}m";
    }

    private static string Ago(DateTimeOffset at)
    {
        var d = DateTimeOffset.Now - at;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} hour{((int)d.TotalHours == 1 ? "" : "s")} ago";
        if (d.TotalDays < 7) return $"{(int)d.TotalDays} day{((int)d.TotalDays == 1 ? "" : "s")} ago";
        return $"on {at.LocalDateTime:d MMM}";
    }

    private static string Clip(string text, float width)
    {
        if (Charts.TextWidth(text) <= width) return text;
        var cut = text;
        while (cut.Length > 1 && Charts.TextWidth(cut + "…") > width) cut = cut[..^1];
        return cut + "…";
    }
}
