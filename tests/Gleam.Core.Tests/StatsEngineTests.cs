using Gleam.Core.Execution;
using Gleam.Core.Logging;
using Gleam.Core.Model;
using Gleam.Core.Stats;

namespace Gleam.Core.Tests;

/// <summary>Every figure on the stats page, held to its one definition.</summary>
public class StatsEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static RunLogEntry Clean(DateTimeOffset at, ActionKind action, ContainerKind from = ContainerKind.Inventory, long gil = 0, ulong who = 1, string rule = "vendor-only-junk") =>
        new(at, who, "Someone", from, 0, 5, "Thing", 1, false, action, rule, gil, ActionOutcome.Done);

    private static MoveLogEntry Move(DateTimeOffset at, ContainerKind from, ContainerKind to, string leg, ulong fromOwner = 0, ulong toOwner = 0) =>
        new(at, 1, "Someone", 6, "Coat", 1, false, from, fromOwner, to, toOwner, leg, "Spare gear");

    private static StatsSnapshot Compute(StatsInput input, StatsRange range = StatsRange.Month, ulong? who = null, TimeZoneInfo? zone = null, DateTimeOffset? countFrom = null) =>
        StatsEngine.Compute(input, new StatsQuery(range, who, Now, zone ?? TimeZoneInfo.Utc, countFrom));

    [Fact]
    public void Every_headline_figure_follows_its_definition()
    {
        var at = Now.AddHours(-1);
        var input = new StatsInput
        {
            Cleaned =
            [
                Clean(at, ActionKind.Discard),
                Clean(at, ActionKind.VendorSell, ContainerKind.Saddlebag, gil: 1200),
                Clean(at, ActionKind.MarketList, ContainerKind.Retainer, gil: 50_000),
                Clean(at, ActionKind.Discard, ContainerKind.GlamourDresser),
                Clean(at, ActionKind.ExpertDelivery),
            ],
            Journal = [new SealsEvent { At = at, CharacterId = 1, ItemId = 5, Seals = 200 }],
        };

        var c = Compute(input).Current;

        Assert.Equal(4, c.SlotsFreed);
        Assert.Equal(1, c.DresserFreed);
        Assert.Equal(1200, c.GilFromSales);
        Assert.Equal(1, c.Listings);
        Assert.Equal(50_000, c.ListedValue);
        Assert.Equal(200, c.Seals);
        Assert.Equal(4 + 3 + 12 + 4 + 6, c.SecondsSaved);
    }

    [Fact]
    public void A_day_is_the_players_own_calendar_day()
    {
        var plusTen = TimeZoneInfo.CreateCustomTimeZone("plus-ten", TimeSpan.FromHours(10), "plus-ten", "plus-ten");
        var input = new StatsInput
        {
            Cleaned =
            [
                Clean(new DateTimeOffset(2026, 9, 9, 20, 0, 0, TimeSpan.Zero), ActionKind.Discard),   // 06:00 on the 10th there
                Clean(new DateTimeOffset(2026, 9, 9, 13, 0, 0, TimeSpan.Zero), ActionKind.Discard),   // 23:00 on the 9th there
            ],
        };

        var s = Compute(input, StatsRange.Week, zone: plusTen);

        Assert.Equal(1, s.Activity[^1].Handled);
        Assert.Equal(1, s.Activity[^2].Handled);
    }

    [Fact]
    public void Changes_are_measured_against_the_same_length_of_time_just_before()
    {
        var input = new StatsInput
        {
            Cleaned =
            [
                Clean(Now.AddDays(-1), ActionKind.Discard), Clean(Now.AddDays(-2), ActionKind.Discard), Clean(Now.AddDays(-3), ActionKind.Discard),
                Clean(Now.AddDays(-10), ActionKind.Discard),
                Clean(Now.AddDays(-20), ActionKind.Discard),
            ],
        };

        var week = Compute(input, StatsRange.Week);

        Assert.Equal(3, week.Current.Handled);
        Assert.Equal(1, week.Previous!.Handled);
        Assert.Null(Compute(input, StatsRange.AllTime).Previous);
    }

    [Fact]
    public void A_move_through_the_bags_counts_once_and_shows_where_it_really_went()
    {
        var input = new StatsInput
        {
            Moves =
            [
                Move(Now.AddMinutes(-10), ContainerKind.Retainer, ContainerKind.Inventory, "RelayOut", fromOwner: 0xA),
                Move(Now.AddMinutes(-9), ContainerKind.Inventory, ContainerKind.Saddlebag, "RelayIn"),
            ],
        };

        var s = Compute(input);

        Assert.Equal(1, s.Current.Moved);
        Assert.Equal(2 * StatsEngine.Cost.MoveLeg, s.Current.SecondsSaved);
        var flow = Assert.Single(s.Flows);
        Assert.Equal((ContainerKind.Retainer, 0xAul, ContainerKind.Saddlebag, 1), (flow.FromKind, flow.FromOwner, flow.ToKind, flow.Count));
    }

    [Fact]
    public void Reset_counts_from_the_moment_it_was_pressed()
    {
        var input = new StatsInput { Cleaned = [Clean(Now.AddDays(-5), ActionKind.Discard), Clean(Now.AddDays(-1), ActionKind.Discard)] };

        var s = Compute(input, countFrom: Now.AddDays(-2));

        Assert.Equal(1, s.Current.Handled);
        Assert.Equal(1, s.AllTimeHandled);
    }

    [Fact]
    public void Success_counts_done_against_failed_and_names_the_commonest_reason()
    {
        var input = new StatsInput
        {
            Journal =
            [
                new RunEvent { At = Now.AddDays(-1), CharacterId = 1, Done = 97, Failed = 2, FailureReasons = ["the game hid the option"] },
                new RunEvent { At = Now.AddHours(-2), CharacterId = 1, Done = 1, Failed = 0, Skipped = 5 },
                new RunEvent { At = Now.AddHours(-1), CharacterId = 1, Failed = 0, FailureReasons = ["the game hid the option", "a window was open"] },
            ],
        };

        var s = Compute(input);

        Assert.Equal(98d / 100, s.SuccessRate!.Value, 6);
        Assert.Equal("the game hid the option", s.TopFailure);
    }

    [Fact]
    public void Milestones_count_every_character_whichever_one_is_shown()
    {
        var at = Now.AddHours(-1);
        var input = new StatsInput
        {
            Cleaned = Enumerable.Range(0, 600).Select(_ => Clean(at, ActionKind.Discard, who: 1))
                .Concat(Enumerable.Range(0, 500).Select(_ => Clean(at, ActionKind.Discard, who: 2))).ToList(),
        };

        var s = Compute(input, who: 1);

        Assert.Equal(600, s.Current.SlotsFreed);
        Assert.True(s.Milestones.Single(m => m.Id == "slots-1000").Earned);
        Assert.False(s.Milestones.Single(m => m.Id == "slots-5000").Earned);
    }

    [Fact]
    public void Streaks_count_consecutive_days_and_the_one_still_going()
    {
        var today = new DateOnly(2026, 9, 10);
        var days = new[] { 0, 1, 2 }.Concat(Enumerable.Range(5, 6)).Select(n => today.AddDays(-n));

        var (longest, current) = StatsEngine.Streaks(days, today);

        Assert.Equal(6, longest);
        Assert.Equal(3, current);
    }

    [Fact]
    public void The_year_starts_on_a_monday_and_ends_today()
    {
        var s = Compute(new StatsInput { Cleaned = [Clean(Now, ActionKind.Discard)] });

        Assert.Equal(DayOfWeek.Monday, s.YearStart.DayOfWeek);
        Assert.Equal(s.To.DayNumber - s.YearStart.DayNumber + 1, s.Year.Count);
        Assert.Equal(1, s.Year[^1]);
    }

    [Fact]
    public async Task Journal_lines_come_back_as_the_kind_they_were_written()
    {
        var storage = new MemoryStorage();
        var journal = new JsonLinesJournal(storage, "journal.jsonl");
        await journal.AppendAsync(new RunEvent { At = Now, CharacterId = 1, Trigger = RunTrigger.HandsFree, Legs = [new LegRecord("Retainers", 168, true)] });
        await journal.AppendAsync(new ScanEvent { At = Now, CharacterId = 1, Used = new() { [ContainerKind.Inventory] = 100 } });

        var back = await new JsonLinesJournal(storage, "journal.jsonl").ReadAllAsync();

        var run = Assert.IsType<RunEvent>(back[0]);
        Assert.Equal("Retainers", Assert.Single(run.Legs).Name);
        Assert.Equal(100, Assert.IsType<ScanEvent>(back[1]).Used[ContainerKind.Inventory]);
    }

    [Fact]
    public async Task A_line_from_a_newer_version_is_skipped()
    {
        var storage = new MemoryStorage();
        storage.Text = "{\"kind\":\"something-new\",\"At\":\"2026-09-10T12:00:00+00:00\"}\n";
        await new JsonLinesJournal(storage, "journal.jsonl").AppendAsync(new MilestoneEvent { At = Now, Id = "first-clean" });

        var back = await new JsonLinesJournal(storage, "journal.jsonl").ReadAllAsync();

        Assert.IsType<MilestoneEvent>(Assert.Single(back));
    }

    private sealed class MemoryStorage : ITextStorage
    {
        public string? Text;
        public bool Exists(string path) => Text is not null;
        public Task<string?> ReadAsync(string path) => Task.FromResult(Text);
        public Task WriteAsync(string path, string contents) { Text = contents; return Task.CompletedTask; }
        public Task AppendAsync(string path, string text) { Text = (Text ?? string.Empty) + text; return Task.CompletedTask; }
    }
}
