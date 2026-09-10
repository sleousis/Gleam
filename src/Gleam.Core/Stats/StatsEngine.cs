using Gleam.Core.Execution;
using Gleam.Core.Logging;
using Gleam.Core.Model;

namespace Gleam.Core.Stats;

public enum StatsRange { Week, Month, Year, AllTime }

/// <summary>What happened to an item, as the stats page colours it. The order is the stacking order of the bars.</summary>
public enum Activity { Discarded, Sold, Listed, TurnedIn, Desynthed, Moved }

public sealed class StatsInput
{
    public IReadOnlyList<RunLogEntry> Cleaned { get; init; } = Array.Empty<RunLogEntry>();
    public IReadOnlyList<MoveLogEntry> Moves { get; init; } = Array.Empty<MoveLogEntry>();
    public IReadOnlyList<JournalEvent> Journal { get; init; } = Array.Empty<JournalEvent>();
}

/// <param name="Character">Null counts every character.</param>
/// <param name="CountFrom">Set by "Reset statistics": nothing before it counts. The histories keep it all.</param>
public sealed record StatsQuery(StatsRange Range, ulong? Character, DateTimeOffset Now, TimeZoneInfo Zone, DateTimeOffset? CountFrom = null);

/// <summary>The headline figures for a stretch of time. Each has exactly one definition, tested in StatsEngineTests.</summary>
public sealed class Totals
{
    /// <summary>Stacks cleaned out of bags, armoury, saddlebag or a retainer: one slot each.</summary>
    public int SlotsFreed { get; set; }
    /// <summary>Items taken out of the glamour dresser, counted apart because the dresser is not bag space.</summary>
    public int DresserFreed { get; set; }
    /// <summary>Gil actually received: sales to a merchant or a retainer.</summary>
    public long GilFromSales { get; set; }
    /// <summary>Market listings and their asking price. Never "earned": whether they sold is not known.</summary>
    public int Listings { get; set; }
    public long ListedValue { get; set; }
    public int Seals { get; set; }
    public int Discarded { get; set; }
    public int Sold { get; set; }
    public int TurnedIn { get; set; }
    public int Desynthed { get; set; }
    /// <summary>Items the organizer put away; a move through the bags counts once.</summary>
    public int Moved { get; set; }
    /// <summary>An estimate from <see cref="StatsEngine.Cost"/>, shown as one.</summary>
    public long SecondsSaved { get; set; }

    public int Cleaned => SlotsFreed + DresserFreed;
    public int Handled => Cleaned + Moved;
}

/// <summary>One bar: a day, or a week or month when the period is long.</summary>
public sealed class Bucket
{
    public DateOnly Start { get; init; }
    public int Days { get; init; }
    public int[] Counts { get; } = new int[6];
    public int Slots { get; set; }
    public long Gil { get; set; }
    public int Listings { get; set; }
    public long SecondsSaved { get; set; }
    public int Handled => Counts.Sum();
}

public sealed record RoomPoint(DateTimeOffset At, int BagsUsed, int BagsCapacity, int? SaddleUsed, int SaddleCapacity);

public sealed record RuleLine(string RuleId, int Suggested, int Kept, int Cleaned, long Gil)
{
    public double KeptShare => Suggested == 0 ? 1 : (double)Kept / Suggested;
}

public sealed record Flow(ContainerKind FromKind, ulong FromOwner, ContainerKind ToKind, ulong ToOwner, int Count);

public sealed record MilestoneState(string Id, string Title, long Progress, long Target)
{
    public bool Earned => Progress >= Target;
}

public sealed class StatsSnapshot
{
    public required StatsQuery Query { get; init; }
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public Totals Current { get; } = new();
    /// <summary>The same length of time just before; null for all time.</summary>
    public Totals? Previous { get; set; }
    public List<Bucket> Activity { get; } = new();
    public int BucketDays { get; set; } = 1;
    public Dictionary<ContainerKind, int> Sources { get; } = new();
    public List<RoomPoint> Room { get; } = new();
    public List<DateTimeOffset> RunMarks { get; } = new();
    public List<RuleLine> Rules { get; } = new();
    public List<Flow> Flows { get; } = new();
    /// <summary>The newest hands-free trip, whenever it was.</summary>
    public RunEvent? LastTrip { get; set; }
    /// <summary>The newest run of any kind, whenever it was.</summary>
    public RunEvent? LastRun { get; set; }
    public int Done { get; set; }
    public int Failed { get; set; }
    public string? TopFailure { get; set; }
    public double? SuccessRate => Done + Failed == 0 ? null : (double)Done / (Done + Failed);
    /// <summary>One value per day from <see cref="YearStart"/> (a Monday) to today: items handled.</summary>
    public DateOnly YearStart { get; set; }
    public List<int> Year { get; } = new();
    public List<MilestoneState> Milestones { get; } = new();
    public int LongestStreak { get; set; }
    public int CurrentStreak { get; set; }
    public DateOnly? HistorySince { get; set; }
    public DateOnly? JournalSince { get; set; }
    public long AllTimeHandled { get; set; }
}

/// <summary>
/// Everything the stats page shows, worked out from the histories and the journal. Pure: the same input
/// gives the same numbers, which is what lets every definition on the page be a test.
/// </summary>
public static class StatsEngine
{
    /// <summary>
    /// Seconds the player would have spent doing each thing by hand. Rough on purpose, and shown as an
    /// estimate with this formula beside it.
    /// </summary>
    public static class Cost
    {
        public const int Discard = 4;
        public const int Sell = 3;
        public const int List = 12;
        public const int TurnIn = 6;
        public const int Desynth = 5;
        public const int MoveLeg = 3;
        public const int RetainerVisit = 20;
    }

    private static readonly (string Id, string Title, long Target)[] MilestoneDefs =
    [
        ("first-clean", "First clean", 1),
        ("slots-1000", "1,000 bag slots freed", 1_000),
        ("slots-5000", "5,000 bag slots freed", 5_000),
        ("gil-100k", "100,000 gil from sales", 100_000),
        ("gil-1m", "1,000,000 gil from sales", 1_000_000),
        ("listings-100", "100 market listings", 100),
        ("moves-500", "500 items put away", 500),
        ("retainers-10", "Ten retainers in one trip", 10),
        ("streak-7", "A week of daily use", 7),
    ];

    public static IReadOnlyList<(string Id, string Title, long Target)> Milestones => MilestoneDefs;

    public static DateOnly LocalDay(DateTimeOffset at, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);

    public static StatsSnapshot Compute(StatsInput input, StatsQuery q)
    {
        var zone = q.Zone;
        var today = LocalDay(q.Now, zone);
        bool Counted(DateTimeOffset at) => q.CountFrom is null || at >= q.CountFrom;
        bool Mine(ulong c) => q.Character is null || q.Character == c;

        var allCleaned = input.Cleaned.Where(e => e.Outcome == ActionOutcome.Done && Counted(e.At)).ToList();
        var allMoves = input.Moves.Where(e => Counted(e.At)).ToList();
        var allJournal = input.Journal.Where(e => Counted(e.At)).ToList();
        var cleaned = allCleaned.Where(e => Mine(e.CharacterId)).ToList();
        var moves = allMoves.Where(e => Mine(e.CharacterId)).ToList();
        var journal = allJournal.Where(e => Mine(e.CharacterId)).ToList();

        var s = new StatsSnapshot { Query = q, To = today };
        var historyDays = cleaned.Select(e => LocalDay(e.At, zone)).Concat(moves.Select(m => LocalDay(m.At, zone))).ToList();
        s.HistorySince = historyDays.Count == 0 ? null : historyDays.Min();
        s.JournalSince = journal.Count == 0 ? null : journal.Min(e => LocalDay(e.At, zone));
        var first = new[] { s.HistorySince, s.JournalSince }.Where(d => d is not null).Select(d => d!.Value).DefaultIfEmpty(today).Min();

        // ---- the period and its bars ----
        var (days, bucketDays) = q.Range switch
        {
            StatsRange.Week => (7, 1),
            StatsRange.Month => (30, 1),
            StatsRange.Year => (364, 7),
            _ => AllTimeShape(first, today),
        };
        var buckets = (days + bucketDays - 1) / bucketDays;
        s.BucketDays = bucketDays;
        s.From = today.AddDays(-(buckets * bucketDays) + 1);
        for (var i = 0; i < buckets; i++) s.Activity.Add(new Bucket { Start = s.From.AddDays(i * bucketDays), Days = bucketDays });
        Bucket? BucketOf(DateOnly d)
        {
            if (d < s.From || d > today) return null;
            return s.Activity[(d.DayNumber - s.From.DayNumber) / bucketDays];
        }
        bool InRange(DateTimeOffset at) { var d = LocalDay(at, zone); return d >= s.From && d <= today; }

        foreach (var e in cleaned)
        {
            var bucket = BucketOf(LocalDay(e.At, zone));
            if (bucket is null) continue;
            Add(s.Current, e);
            s.Sources[e.Container] = s.Sources.GetValueOrDefault(e.Container) + 1;
            bucket.Counts[(int)ActivityOf(e.Action)]++;
            if (e.Container != ContainerKind.GlamourDresser) bucket.Slots++;
            if (e.Action == ActionKind.VendorSell) bucket.Gil += e.ValueGil;
            if (e.Action == ActionKind.MarketList) bucket.Listings++;
            bucket.SecondsSaved += SecondsFor(e.Action);
        }
        foreach (var m in moves)
        {
            var bucket = BucketOf(LocalDay(m.At, zone));
            if (bucket is null) continue;
            Add(s.Current, m);
            if (m.Leg != "RelayOut") bucket.Counts[(int)Activity.Moved]++;
            bucket.SecondsSaved += Cost.MoveLeg;
        }
        s.Flows.AddRange(Flows(moves.Where(m => InRange(m.At))));

        // ---- the journal ----
        var rules = new Dictionary<string, (int Suggested, int Kept, int Cleaned, long Gil)>();
        var reasons = new Dictionary<string, int>();
        foreach (var e in journal.OrderBy(e => e.At))
        {
            switch (e)
            {
                case RunEvent run:
                    if (run.IsTrip) s.LastTrip = run;
                    s.LastRun = run;
                    if (!InRange(run.At)) break;
                    s.Done += run.Done;
                    s.Failed += run.Failed;
                    foreach (var r in run.FailureReasons) reasons[r] = reasons.GetValueOrDefault(r) + 1;
                    s.RunMarks.Add(run.At);
                    s.Current.SecondsSaved += run.RetainersVisited * Cost.RetainerVisit;
                    if (BucketOf(LocalDay(run.At, zone)) is { } rb) rb.SecondsSaved += run.RetainersVisited * Cost.RetainerVisit;
                    break;
                case ScanEvent scan when InRange(scan.At):
                    s.Room.Add(new RoomPoint(scan.At,
                        scan.Used.GetValueOrDefault(ContainerKind.Inventory), scan.Capacity.GetValueOrDefault(ContainerKind.Inventory, 140),
                        scan.Used.TryGetValue(ContainerKind.Saddlebag, out var saddle) ? saddle : null, scan.Capacity.GetValueOrDefault(ContainerKind.Saddlebag, 70)));
                    break;
                case DecisionEvent decision when InRange(decision.At):
                    foreach (var t in decision.Rules)
                    {
                        var r = rules.GetValueOrDefault(t.RuleId);
                        rules[t.RuleId] = (r.Suggested + t.Suggested, r.Kept + t.Kept, r.Cleaned, r.Gil);
                    }
                    break;
                case SealsEvent seals when InRange(seals.At):
                    s.Current.Seals += seals.Seals;
                    break;
            }
        }
        s.TopFailure = reasons.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
        foreach (var e in cleaned.Where(e => InRange(e.At) && e.RuleId != Planning.RunPlanner.HandPickRuleId))
        {
            var r = rules.GetValueOrDefault(e.RuleId);
            rules[e.RuleId] = (r.Suggested, r.Kept, r.Cleaned + 1, r.Gil + (e.Action == ActionKind.VendorSell ? e.ValueGil : 0));
        }
        s.Rules.AddRange(rules.Select(kv => new RuleLine(kv.Key, kv.Value.Suggested, kv.Value.Kept, kv.Value.Cleaned, kv.Value.Gil))
            .OrderByDescending(r => r.Suggested).ThenByDescending(r => r.Cleaned));
        Thin(s.Room, 120);

        // ---- the period before, for the changes ----
        if (q.Range != StatsRange.AllTime)
        {
            var prevTo = s.From.AddDays(-1);
            var prevFrom = s.From.AddDays(-(buckets * bucketDays));
            bool InPrev(DateTimeOffset at) { var d = LocalDay(at, zone); return d >= prevFrom && d <= prevTo; }
            var prev = new Totals();
            foreach (var e in cleaned.Where(e => InPrev(e.At))) Add(prev, e);
            foreach (var m in moves.Where(m => InPrev(m.At))) Add(prev, m);
            foreach (var run in journal.OfType<RunEvent>().Where(r => InPrev(r.At))) prev.SecondsSaved += run.RetainersVisited * Cost.RetainerVisit;
            foreach (var seals in journal.OfType<SealsEvent>().Where(e => InPrev(e.At))) prev.Seals += seals.Seals;
            s.Previous = prev;
        }

        // ---- a year of days, and streaks ----
        var perDay = new Dictionary<DateOnly, int>();
        foreach (var e in cleaned) { var d = LocalDay(e.At, zone); perDay[d] = perDay.GetValueOrDefault(d) + 1; }
        foreach (var m in moves.Where(m => m.Leg != "RelayOut")) { var d = LocalDay(m.At, zone); perDay[d] = perDay.GetValueOrDefault(d) + 1; }
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        s.YearStart = monday.AddDays(-52 * 7);
        for (var d = s.YearStart; d <= today; d = d.AddDays(1)) s.Year.Add(perDay.GetValueOrDefault(d));
        (s.LongestStreak, s.CurrentStreak) = Streaks(perDay.Keys, today);

        // ---- milestones: every character, all time ----
        var all = new Totals();
        foreach (var e in allCleaned) Add(all, e);
        foreach (var m in allMoves) Add(all, m);
        var allDays = allCleaned.Select(e => LocalDay(e.At, zone)).Concat(allMoves.Select(m => LocalDay(m.At, zone))).ToHashSet();
        var bestTrip = allJournal.OfType<RunEvent>().Select(r => r.RetainersVisited).DefaultIfEmpty(0).Max();
        var longest = Streaks(allDays, today).Longest;
        s.AllTimeHandled = all.Handled;
        foreach (var (id, title, target) in MilestoneDefs)
        {
            long value = id switch
            {
                "first-clean" => all.Cleaned,
                "slots-1000" or "slots-5000" => all.SlotsFreed,
                "gil-100k" or "gil-1m" => all.GilFromSales,
                "listings-100" => all.Listings,
                "moves-500" => all.Moved,
                "retainers-10" => bestTrip,
                "streak-7" => longest,
                _ => 0,
            };
            s.Milestones.Add(new MilestoneState(id, title, Math.Min(value, target), target));
        }
        return s;
    }

    /// <summary>All time in at most about 120 bars: weeks while they fit, months after that.</summary>
    private static (int Days, int BucketDays) AllTimeShape(DateOnly first, DateOnly today)
    {
        var span = Math.Max(7, today.DayNumber - first.DayNumber + 1);
        return span <= 120 * 7 ? (span, 7) : (span, 30);
    }

    public static Activity ActivityOf(ActionKind action) => action switch
    {
        ActionKind.VendorSell => Activity.Sold,
        ActionKind.MarketList => Activity.Listed,
        ActionKind.ExpertDelivery => Activity.TurnedIn,
        ActionKind.Desynth => Activity.Desynthed,
        _ => Activity.Discarded,
    };

    private static int SecondsFor(ActionKind action) => action switch
    {
        ActionKind.Discard => Cost.Discard,
        ActionKind.VendorSell => Cost.Sell,
        ActionKind.MarketList => Cost.List,
        ActionKind.ExpertDelivery => Cost.TurnIn,
        ActionKind.Desynth => Cost.Desynth,
        _ => 0,
    };

    private static void Add(Totals t, RunLogEntry e)
    {
        if (e.Container == ContainerKind.GlamourDresser) t.DresserFreed++;
        else t.SlotsFreed++;
        switch (e.Action)
        {
            case ActionKind.Discard: t.Discarded++; break;
            case ActionKind.VendorSell: t.Sold++; t.GilFromSales += e.ValueGil; break;
            case ActionKind.MarketList: t.Listings++; t.ListedValue += e.ValueGil; break;
            case ActionKind.ExpertDelivery: t.TurnedIn++; break;
            case ActionKind.Desynth: t.Desynthed++; break;
        }
        t.SecondsSaved += SecondsFor(e.Action);
    }

    private static void Add(Totals t, MoveLogEntry m)
    {
        // The two halves of a move through the bags are one item put away, and two moves of work saved.
        if (m.Leg != "RelayOut") t.Moved++;
        t.SecondsSaved += Cost.MoveLeg;
    }

    /// <summary>
    /// Where things went, storage to storage. A move through the bags is logged as two halves; each second
    /// half is joined to the first half of the same stack, so it shows as retainer to saddlebag, not bags to saddlebag.
    /// </summary>
    public static IEnumerable<Flow> Flows(IEnumerable<MoveLogEntry> moves)
    {
        var counts = new Dictionary<(ContainerKind, ulong, ContainerKind, ulong), int>();
        var outs = new Dictionary<(uint, int, bool), Queue<MoveLogEntry>>();
        void Count(ContainerKind fk, ulong fo, ContainerKind tk, ulong to) => counts[(fk, fo, tk, to)] = counts.GetValueOrDefault((fk, fo, tk, to)) + 1;
        foreach (var m in moves.OrderBy(m => m.At))
        {
            var key = (m.ItemId, m.Quantity, m.IsHq);
            switch (m.Leg)
            {
                case "RelayOut":
                    if (!outs.TryGetValue(key, out var q)) outs[key] = q = new Queue<MoveLogEntry>();
                    q.Enqueue(m);
                    break;
                case "RelayIn" when outs.TryGetValue(key, out var waiting) && waiting.Count > 0:
                    var first = waiting.Dequeue();
                    Count(first.FromKind, first.FromOwner, m.ToKind, m.ToOwner);
                    break;
                default:
                    Count(m.FromKind, m.FromOwner, m.ToKind, m.ToOwner);
                    break;
            }
        }
        // A first half with no second half yet: the stack is waiting in the bags.
        foreach (var m in outs.Values.SelectMany(q => q)) Count(m.FromKind, m.FromOwner, ContainerKind.Inventory, 0);
        return counts.Select(kv => new Flow(kv.Key.Item1, kv.Key.Item2, kv.Key.Item3, kv.Key.Item4, kv.Value)).OrderByDescending(f => f.Count);
    }

    /// <summary>Longest run of consecutive active days, and the one ending today or yesterday.</summary>
    public static (int Longest, int Current) Streaks(IEnumerable<DateOnly> activeDays, DateOnly today)
    {
        var days = activeDays.Distinct().OrderBy(d => d).ToList();
        int longest = 0, run = 0;
        DateOnly? prev = null;
        foreach (var d in days)
        {
            run = prev is { } p && d.DayNumber == p.DayNumber + 1 ? run + 1 : 1;
            longest = Math.Max(longest, run);
            prev = d;
        }
        var current = 0;
        if (prev is { } last && today.DayNumber - last.DayNumber <= 1) current = run;
        return (longest, current);
    }

    private static void Thin<T>(List<T> list, int max)
    {
        if (list.Count <= max) return;
        var step = (double)list.Count / max;
        var kept = Enumerable.Range(0, max).Select(i => list[(int)(i * step)]).ToList();
        kept[^1] = list[^1];
        list.Clear();
        list.AddRange(kept);
    }
}
