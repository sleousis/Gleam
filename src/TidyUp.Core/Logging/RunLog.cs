using System.Text.Json;
using System.Text.Json.Serialization;
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
}

/// <summary>JSON-lines log. Small enough to rewrite whole on each append, which keeps it a single durable write.</summary>
public sealed class JsonLinesRunLog : IRunLog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly ITextStorage storage;
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private List<RunLogEntry>? cache;

    public JsonLinesRunLog(ITextStorage storage, string path)
    {
        this.storage = storage;
        this.path = path;
    }

    public async Task AppendAsync(RunLogEntry entry)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var all = await LoadAsync().ConfigureAwait(false);
            all.Add(entry);
            var text = string.Join('\n', all.Select(e => JsonSerializer.Serialize(e, JsonOptions))) + "\n";
            await storage.WriteAsync(path, text).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<RunLogEntry>> ReadAllAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return (await LoadAsync().ConfigureAwait(false)).ToList();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<List<RunLogEntry>> LoadAsync()
    {
        if (cache is not null) return cache;
        // Nothing is cached until the file has actually been read. A read that threw used to leave an
        // empty list cached, and the next append wrote that empty list over the whole history.
        var loaded = new List<RunLogEntry>();
        if (!storage.Exists(path)) return cache = loaded;
        var text = await storage.ReadAsync(path).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return cache = loaded;
        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var e = JsonSerializer.Deserialize<RunLogEntry>(line, JsonOptions);
                if (e is not null) loaded.Add(e);
            }
            catch (JsonException)
            {
                // A corrupt line must never take the whole history with it.
            }
        }
        return cache = loaded;
    }
}

/// <summary>In-memory log for tests and for the debug window.</summary>
public sealed class MemoryRunLog : IRunLog
{
    public List<RunLogEntry> Entries { get; } = new();
    public Task AppendAsync(RunLogEntry entry) { Entries.Add(entry); return Task.CompletedTask; }
    public Task<IReadOnlyList<RunLogEntry>> ReadAllAsync() => Task.FromResult<IReadOnlyList<RunLogEntry>>(Entries.ToList());
}
