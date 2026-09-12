using System.Collections.Concurrent;
using Dalamud.Plugin.Services;
using Gleam.Core.Execution;
using Gleam.Core.Logging;
using Gleam.Core.Model;
using Gleam.Core.Organizer.Capacity;
using Gleam.Core.Organizer.Execution;
using Gleam.Core.Planning;
using Gleam.Core.Stats;

namespace Gleam.Services;

/// <summary>
/// Keeps the stats page's numbers current. The histories and the journal are read once, when Gleam
/// starts; after that every finished item, move, run and scan is added as it happens, and the snapshot is
/// worked out again in the background, a few times a second at most. Nothing is read from disk twice.
///
/// Also writes the journal: what runs and trips looked like, how full the bags were, which rules the
/// player kept, seals, and milestones once reached.
/// </summary>
public sealed class StatsService : IDisposable
{
    private readonly IFramework framework;
    private readonly IPlayerState player;
    private readonly IChatGui chat;
    private readonly IPluginLog log;
    private readonly Configuration config;
    private readonly IRunLog runLog;
    private readonly IMoveLog moveLog;
    private readonly IJournal journal;
    private readonly Action save;

    private readonly object gate = new();
    private readonly List<RunLogEntry> cleaned = new();
    private readonly List<MoveLogEntry> moves = new();
    private readonly List<JournalEvent> events = new();
    private readonly Queue<DateTime> finishedAt = new();
    private readonly float[] rate = new float[40];

    private bool loading;
    private volatile bool dirty = true;
    private volatile bool computing;
    private DateTime lastCompute = DateTime.MinValue;
    private DateTime lastSample = DateTime.UtcNow;
    private ulong character;
    private string characterName = string.Empty;

    // The sums are only worth doing for a page someone is looking at, or to see whether a finished run
    // reached a milestone. Recounting every history a few times a second in the background, for a page
    // that was not open, cost the most exactly when the game was busiest.
    private long viewedAt = long.MinValue;
    private volatile bool milestonesDue = true;
    private (StatsRange, bool, ulong)? computedFor;

    /// <summary>Called by the stats page each time it draws.</summary>
    public void MarkViewed() => viewedAt = Environment.TickCount64;

    private bool Viewed => Environment.TickCount64 - viewedAt < 1000;

    public StatsService(IFramework framework, IPlayerState player, IChatGui chat, IPluginLog log, Configuration config,
        IRunLog runLog, IMoveLog moveLog, IJournal journal, Action save)
    {
        this.framework = framework;
        this.player = player;
        this.chat = chat;
        this.log = log;
        this.config = config;
        this.runLog = runLog;
        this.moveLog = moveLog;
        this.journal = journal;
        this.save = save;
        framework.Update += Tick;
        EnsureLoaded();
    }

    public void Dispose() => framework.Update -= Tick;

    public bool Loaded { get; private set; }
    public bool LoadFailed { get; private set; }

    /// <summary>The numbers for the period and character the page is set to. Null until the first sums are done.</summary>
    public StatsSnapshot? Snapshot { get; private set; }

    /// <summary>Milestones reached while Gleam was running, and when, so the page can make them shine once.</summary>
    public ConcurrentDictionary<string, DateTime> FreshMilestones { get; } = new();

    /// <summary>Items finished per minute, one sample a second, oldest first.</summary>
    public IReadOnlyList<float> Rate => rate;
    public float CurrentRate => rate[^1];

    /// <summary>How far through the current second the rate line is, so it scrolls instead of stepping.</summary>
    public float RateScroll => (float)Math.Clamp((DateTime.UtcNow - lastSample).TotalSeconds, 0, 1);

    public StatsRange Range
    {
        get => (StatsRange)Math.Clamp(config.StatsRange, 0, 3);
        set
        {
            if (config.StatsRange == (int)value) return;
            config.StatsRange = (int)value;
            save();
            Invalidate();
        }
    }

    public bool AllCharacters
    {
        get => config.StatsAllCharacters;
        set
        {
            if (config.StatsAllCharacters == value) return;
            config.StatsAllCharacters = value;
            save();
            Invalidate();
        }
    }

    public void Invalidate() => dirty = true;

    public void EnsureLoaded()
    {
        if (Loaded || loading) return;
        loading = true;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var c = await runLog.ReadAllAsync().ConfigureAwait(false);
            var m = await moveLog.ReadAllAsync().ConfigureAwait(false);
            var j = await journal.ReadAllAsync().ConfigureAwait(false);
            lock (gate)
            {
                cleaned.InsertRange(0, c);
                moves.InsertRange(0, m);
                events.InsertRange(0, j);
            }
            Loaded = true;
            Invalidate();
        }
        catch (Exception ex)
        {
            LoadFailed = true;
            log.Warning(ex, "The stats could not read the histories");
        }
        finally
        {
            loading = false;
        }
    }

    /// <summary>The game's thread: note who is logged in, sample the rate, and start a recount when something changed.</summary>
    private void Tick(IFramework fw)
    {
        // The name is only read again when someone else logs in, or while it has not loaded yet.
        var id = player.ContentId;
        if (id != character || (id != 0 && characterName.Length == 0))
        {
            character = id;
            characterName = player.CharacterName;
        }

        var now = DateTime.UtcNow;
        if ((now - lastSample).TotalSeconds >= 1)
        {
            lastSample = now;
            int lastHalfMinute;
            lock (gate)
            {
                while (finishedAt.Count > 0 && (now - finishedAt.Peek()).TotalSeconds > 30) finishedAt.Dequeue();
                lastHalfMinute = finishedAt.Count;
            }
            Array.Copy(rate, 1, rate, 0, rate.Length - 1);
            rate[^1] = lastHalfMinute * 2f;
        }

        if (!dirty || !Loaded || computing) return;
        var viewed = Viewed;
        if (!viewed && !milestonesDue) return;
        // A different period or character on the page is answered at once; anything else waits its second.
        var asked = (Range, AllCharacters, character);
        if ((now - lastCompute).TotalMilliseconds < 1000 && !(viewed && computedFor != asked)) return;
        dirty = false;
        milestonesDue = false;
        computing = true;
        lastCompute = now;
        computedFor = asked;
        var query = new StatsQuery(Range, AllCharacters ? null : character, DateTimeOffset.Now, TimeZoneInfo.Local, config.StatsResetAt);
        _ = Task.Run(() => Compute(query));
    }

    private void Compute(StatsQuery query)
    {
        try
        {
            StatsInput input;
            lock (gate) input = new StatsInput { Cleaned = cleaned.ToList(), Moves = moves.ToList(), Journal = events.ToList() };
            var snapshot = StatsEngine.Compute(input, query);
            Snapshot = snapshot;
            CheckMilestones(snapshot);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Working out the stats failed");
        }
        finally
        {
            computing = false;
        }
    }

    /// <summary>
    /// A milestone is announced once, the first time it is reached, and written down so it never is again.
    /// Ones already earned when the stats page first counted a player's history are written down quietly.
    /// </summary>
    private void CheckMilestones(StatsSnapshot snapshot)
    {
        HashSet<string> known;
        lock (gate) known = events.OfType<MilestoneEvent>().Select(e => e.Id).ToHashSet();
        var reached = snapshot.Milestones.Where(m => m.Earned && !known.Contains(m.Id)).ToList();
        var quiet = !config.StatsMilestonesPrimed;
        foreach (var m in reached)
        {
            Record(new MilestoneEvent { Id = m.Id });
            if (quiet) continue;
            FreshMilestones[m.Id] = DateTime.UtcNow;
            if (config.ShowMilestones)
                _ = framework.RunOnFrameworkThread(() => chat.Print($"Milestone reached: {m.Title}. /gleam stats shows the rest.", "Gleam"));
        }
        if (quiet) _ = framework.RunOnFrameworkThread(() => { config.StatsMilestonesPrimed = true; save(); });
    }

    /// <summary>Writes one line to the journal and counts it straight away.</summary>
    public void Record(JournalEvent entry)
    {
        var stamped = entry.At == default ? entry with { At = DateTimeOffset.Now } : entry;
        if (stamped.CharacterId == 0) stamped = stamped with { CharacterId = character };
        lock (gate) events.Add(stamped);
        _ = journal.AppendAsync(stamped).ContinueWith(t => log.Warning(t.Exception, "The stats journal could not be written"), TaskContinuationOptions.OnlyOnFaulted);
        Invalidate();
    }

    // ---------- live feed from the runs ----------

    public void OnActionFinished(ActionResult result)
    {
        if (result.Outcome != ActionOutcome.Done) return;
        var entry = RunLogEntry.From(result.Action, new RunIdentity(character, characterName), ActionOutcome.Done);
        lock (gate)
        {
            if (Loaded) cleaned.Add(entry);
            finishedAt.Enqueue(DateTime.UtcNow);
        }
        Invalidate();
    }

    public void OnMoveFinished(MoveResult result)
    {
        if (result.Status != StepStatus.Done) return;
        var op = result.Op;
        var entry = new MoveLogEntry(DateTimeOffset.UtcNow, character, characterName, op.Item.ItemId, op.Info.Name, op.Item.Quantity, op.Item.IsHq,
            op.From.Kind, op.From.OwnerId, op.To.Kind, op.To.OwnerId, op.Leg.ToString(), op.RuleName);
        lock (gate)
        {
            if (Loaded) moves.Add(entry);
            finishedAt.Enqueue(DateTime.UtcNow);
        }
        Invalidate();
    }

    public void OnRunFinished(RunReport report, RunTrigger trigger, DateTimeOffset started)
    {
        // A finished run is when a milestone can have been reached, page open or not.
        milestonesDue = true;
        Invalidate();
        if (trigger == RunTrigger.PartOfTrip || report.Results.Count + report.Pending.Count == 0) return;
        Record(new RunEvent
        {
            Trigger = trigger,
            Started = started,
            Planned = report.Results.Count + report.Pending.Count,
            Done = report.Done,
            Skipped = report.Skipped,
            Failed = report.Failed,
            Waiting = report.Pending.Count,
            Stopped = report.NotReached > 0,
            FailureReasons = report.Results.Where(r => r.Outcome == ActionOutcome.Failed).Select(r => r.Message).Distinct().Take(5).ToList(),
        });
    }

    public void OnMovesFinished(MoveRunReport report, RunTrigger trigger, DateTimeOffset started)
    {
        milestonesDue = true;
        Invalidate();
        if (trigger == RunTrigger.PartOfTrip || report.Results.Count + report.Pending.Count == 0) return;
        Record(new RunEvent
        {
            Trigger = trigger,
            Started = started,
            Planned = report.Results.Count + report.Pending.Count,
            Done = report.Done,
            Skipped = report.Skipped,
            Failed = report.Failed,
            Waiting = report.Pending.Count,
            Stopped = report.NotReached > 0,
            FailureReasons = report.Results.Where(r => r.Status == StepStatus.Failed).Select(r => r.Message).Distinct().Take(5).ToList(),
        });
    }

    public void OnTrip(RunEvent trip)
    {
        milestonesDue = true;
        Record(trip);
    }

    public void OnSeals(uint itemId, int seals)
    {
        if (seals > 0) Record(new SealsEvent { ItemId = itemId, Seals = seals });
    }

    /// <summary>Which rules' picks the player kept ticked when they started a run.</summary>
    public void OnDecisions(RunPlan plan)
    {
        var tallies = plan.AllRows.Where(r => r.IsSuggested && r.IsExecutable)
            .GroupBy(r => r.Proposal.RuleId)
            .Select(g => new RuleTally(g.Key, g.Count(), g.Count(r => r.Checked)))
            .ToList();
        if (tallies.Count > 0) Record(new DecisionEvent { Rules = tallies });
    }

    /// <summary>
    /// How full everything was at a full look through the player's things. A look that finds the same
    /// fullness moments after the last one adds nothing to the chart and is not written down.
    /// </summary>
    public void OnScan(InventorySnapshot snapshot, RunPlan plan)
    {
        var used = new Dictionary<ContainerKind, int>();
        var capacity = new Dictionary<ContainerKind, int>();
        foreach (var group in snapshot.Items.GroupBy(i => i.Slot.Kind)) used[group.Key] = group.Count();
        used.TryAdd(ContainerKind.Inventory, 0);
        capacity[ContainerKind.Inventory] = CapacityModel.PagesOf(ContainerKind.Inventory).Sum(CapacityModel.DefaultPageSize);
        capacity[ContainerKind.Armoury] = CapacityModel.PagesOf(ContainerKind.Armoury).Sum(CapacityModel.DefaultPageSize);

        var saddle = snapshot.Items.Where(i => i.Slot.Kind == ContainerKind.Saddlebag).ToList();
        if (saddle.Count > 0 || snapshot.LiveContainers.Any(c => c.Kind == ContainerKind.Saddlebag))
        {
            used[ContainerKind.Saddlebag] = saddle.Count;
            capacity[ContainerKind.Saddlebag] = saddle.Any(i => i.Slot.ContainerId >= GameContainerIds.PremiumSaddleBag1) ? 140 : 70;
        }
        else used.Remove(ContainerKind.Saddlebag);
        if (snapshot.RetainerNames.Count > 0) capacity[ContainerKind.Retainer] = snapshot.RetainerNames.Count * 175;

        ScanEvent? last;
        lock (gate) last = events.OfType<ScanEvent>().LastOrDefault(e => e.CharacterId == character);
        if (last is not null && (DateTimeOffset.Now - last.At).TotalSeconds < 90
            && last.Used.GetValueOrDefault(ContainerKind.Inventory) == used[ContainerKind.Inventory]
            && last.Used.GetValueOrDefault(ContainerKind.Saddlebag) == used.GetValueOrDefault(ContainerKind.Saddlebag))
            return;

        Record(new ScanEvent { Used = used, Capacity = capacity, Suggested = plan.AllRows.Count(r => r.IsSuggested && r.IsExecutable) });
    }

    /// <summary>"Reset statistics": counting starts again from now. The histories themselves are left alone.</summary>
    public async Task ResetAsync()
    {
        await journal.ClearAsync().ConfigureAwait(false);
        lock (gate) events.Clear();
        FreshMilestones.Clear();
        await framework.RunOnFrameworkThread(() =>
        {
            config.StatsResetAt = DateTimeOffset.Now;
            config.StatsMilestonesPrimed = false;
            save();
        }).ConfigureAwait(false);
        Invalidate();
    }
}
