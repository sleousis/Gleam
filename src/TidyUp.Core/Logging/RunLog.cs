using TidyUp.Core.Execution;
using TidyUp.Core.Model;

namespace TidyUp.Core.Logging;

public sealed record RunIdentity(ulong CharacterId, string CharacterName);

/// <summary>One line of the append-only log: exactly one physical stack that was acted on.</summary>
public sealed record RunLogEntry(
    DateTimeOffset At,
    ulong CharacterId,
    string CharacterName,
    ContainerKind Container,
    ulong OwnerId,
    uint ItemId,
    string ItemName,
    int Quantity,
    bool IsHq,
    ActionKind Action,
    string RuleId,
    long ValueGil,
    ActionOutcome Outcome)
{
    public static RunLogEntry From(QueuedAction a, RunIdentity id, ActionOutcome outcome) => new(
        DateTimeOffset.UtcNow, id.CharacterId, id.CharacterName, a.Kind, a.Slot.OwnerId, a.ItemId, a.ItemName,
        a.Quantity, a.IsHq, a.Action, a.RuleId, a.ValueGil, outcome);
}

public interface IRunLog
{
    Task AppendAsync(RunLogEntry entry);
    Task<IReadOnlyList<RunLogEntry>> ReadAllAsync();
}

/// <summary>Minimal durable text storage; the plugin backs it with Dalamud's IReliableFileStorage.</summary>
public interface ITextStorage
{
    bool Exists(string path);
    Task<string?> ReadAsync(string path);
    Task WriteAsync(string path, string contents);

    /// <summary>Adds text to the end of a file, creating it if needed. Storage that cannot append rewrites the file.</summary>
    async Task AppendAsync(string path, string text)
    {
        var existing = Exists(path) ? await ReadAsync(path).ConfigureAwait(false) : null;
        await WriteAsync(path, (existing ?? string.Empty) + text).ConfigureAwait(false);
    }
}

/// <summary>The cleaner's history: one JSON line per stack acted on.</summary>
public sealed class JsonLinesRunLog : IRunLog
{
    private readonly JsonLinesLog<RunLogEntry> inner;
    public JsonLinesRunLog(ITextStorage storage, string path) => inner = new JsonLinesLog<RunLogEntry>(storage, path);
    public Task AppendAsync(RunLogEntry entry) => inner.AppendAsync(entry);
    public Task<IReadOnlyList<RunLogEntry>> ReadAllAsync() => inner.ReadAllAsync();
}

/// <summary>In-memory log for tests and for the debug window.</summary>
public sealed class MemoryRunLog : IRunLog
{
    public List<RunLogEntry> Entries { get; } = new();
    public Task AppendAsync(RunLogEntry entry) { Entries.Add(entry); return Task.CompletedTask; }
    public Task<IReadOnlyList<RunLogEntry>> ReadAllAsync() => Task.FromResult<IReadOnlyList<RunLogEntry>>(Entries.ToList());
}
