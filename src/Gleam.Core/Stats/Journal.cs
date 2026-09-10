using System.Text.Json.Serialization;
using Gleam.Core.Logging;
using Gleam.Core.Model;

namespace Gleam.Core.Stats;

/// <summary>How a run came about. It decides how the stats page describes it, and whether it is recorded at all.</summary>
public enum RunTrigger
{
    ByHand,
    HandsFree,
    AfterVentures,
    Organize,
    HandsFreeOrganize,
    /// <summary>Moves that were waiting ran because their storage opened.</summary>
    StorageOpened,
    /// <summary>One stop of a hands-free trip. The trip records itself, so its stops record nothing on their own.</summary>
    PartOfTrip,
}

/// <summary>
/// Things the two histories cannot tell: how runs went, how full the bags were, which rules the player
/// kept, what seals came back. One line each in <c>gleam-journal.jsonl</c>, the "kind" first so a line
/// written by a newer version is simply skipped by an older one.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RunEvent), "run")]
[JsonDerivedType(typeof(ScanEvent), "scan")]
[JsonDerivedType(typeof(DecisionEvent), "decisions")]
[JsonDerivedType(typeof(SealsEvent), "seals")]
[JsonDerivedType(typeof(MilestoneEvent), "milestone")]
public abstract record JournalEvent
{
    public DateTimeOffset At { get; init; }
    public ulong CharacterId { get; init; }
}

/// <summary>One stop of a hands-free trip and how long it took.</summary>
public sealed record LegRecord(string Name, double Seconds, bool Ok);

/// <summary>A run, from the press of the button to the last item.</summary>
public sealed record RunEvent : JournalEvent
{
    public RunTrigger Trigger { get; init; }
    public DateTimeOffset Started { get; init; }
    public int Planned { get; init; }
    public int Done { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public int Waiting { get; init; }
    public bool Stopped { get; init; }
    public List<LegRecord> Legs { get; init; } = new();
    public int Teleports { get; init; }
    public double WalkedYalms { get; init; }
    public int RetainersVisited { get; init; }
    public List<string> FailureReasons { get; init; } = new();

    [JsonIgnore]
    public double Seconds => Math.Max(0, (At - Started).TotalSeconds);

    [JsonIgnore]
    public bool IsTrip => Trigger is RunTrigger.HandsFree or RunTrigger.HandsFreeOrganize;
}

/// <summary>How full each storage was when Gleam last looked through everything.</summary>
public sealed record ScanEvent : JournalEvent
{
    public Dictionary<ContainerKind, int> Used { get; init; } = new();
    public Dictionary<ContainerKind, int> Capacity { get; init; } = new();
    public int Suggested { get; init; }
}

/// <summary>What each rule suggested at the moment a run was started, and how much of it the player kept ticked.</summary>
public sealed record RuleTally(string RuleId, int Suggested, int Kept);

public sealed record DecisionEvent : JournalEvent
{
    public List<RuleTally> Rules { get; init; } = new();
}

/// <summary>Grand Company seals, as the delivery window showed them at turn-in.</summary>
public sealed record SealsEvent : JournalEvent
{
    public uint ItemId { get; init; }
    public int Seals { get; init; }
}

/// <summary>A milestone reached, so it is announced once and never again.</summary>
public sealed record MilestoneEvent : JournalEvent
{
    public string Id { get; init; } = string.Empty;
}

public interface IJournal
{
    Task AppendAsync(JournalEvent entry);
    Task<IReadOnlyList<JournalEvent>> ReadAllAsync();
    Task ClearAsync();
}

public sealed class JsonLinesJournal : IJournal
{
    private readonly JsonLinesLog<JournalEvent> inner;
    public JsonLinesJournal(ITextStorage storage, string path) => inner = new JsonLinesLog<JournalEvent>(storage, path);
    public Task AppendAsync(JournalEvent entry) => inner.AppendAsync(entry);
    public Task<IReadOnlyList<JournalEvent>> ReadAllAsync() => inner.ReadAllAsync();
    public Task ClearAsync() => inner.ClearAsync();
}

public sealed class MemoryJournal : IJournal
{
    public List<JournalEvent> Entries { get; } = new();
    public Task AppendAsync(JournalEvent entry) { Entries.Add(entry); return Task.CompletedTask; }
    public Task<IReadOnlyList<JournalEvent>> ReadAllAsync() => Task.FromResult<IReadOnlyList<JournalEvent>>(Entries.ToList());
    public Task ClearAsync() { Entries.Clear(); return Task.CompletedTask; }
}
