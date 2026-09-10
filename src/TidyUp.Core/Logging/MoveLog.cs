using System.Text.Json;
using System.Text.Json.Serialization;
using TidyUp.Core.Model;

namespace TidyUp.Core.Logging;

/// <summary>One completed move, for the organizer's own history.</summary>
public sealed record MoveLogEntry(
    DateTimeOffset At,
    ulong CharacterId,
    string CharacterName,
    uint ItemId,
    string ItemName,
    int Quantity,
    bool IsHq,
    ContainerKind FromKind,
    ulong FromOwner,
    ContainerKind ToKind,
    ulong ToOwner,
    string Leg,
    string RuleName);

public interface IMoveLog
{
    Task AppendAsync(MoveLogEntry entry);
    Task<IReadOnlyList<MoveLogEntry>> ReadAllAsync();
}

/// <summary>JSON-lines append-only log of any record type; same shape and durability as the cleaner's history.</summary>
public sealed class JsonLinesLog<T> where T : class
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly ITextStorage storage;
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private List<T>? cache;

    public JsonLinesLog(ITextStorage storage, string path)
    {
        this.storage = storage;
        this.path = path;
    }

    public async Task AppendAsync(T entry)
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

    public async Task<IReadOnlyList<T>> ReadAllAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { return (await LoadAsync().ConfigureAwait(false)).ToList(); }
        finally { gate.Release(); }
    }

    private async Task<List<T>> LoadAsync()
    {
        if (cache is not null) return cache;
        // Nothing is cached until the file has actually been read. A read that threw used to leave an
        // empty list cached, and the next append wrote that empty list over the whole history.
        var loaded = new List<T>();
        if (!storage.Exists(path)) return cache = loaded;
        var text = await storage.ReadAsync(path).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return cache = loaded;
        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var e = JsonSerializer.Deserialize<T>(line, JsonOptions);
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

public sealed class JsonLinesMoveLog : IMoveLog
{
    private readonly JsonLinesLog<MoveLogEntry> inner;
    public JsonLinesMoveLog(ITextStorage storage, string path) => inner = new JsonLinesLog<MoveLogEntry>(storage, path);
    public Task AppendAsync(MoveLogEntry entry) => inner.AppendAsync(entry);
    public Task<IReadOnlyList<MoveLogEntry>> ReadAllAsync() => inner.ReadAllAsync();
}

public sealed class MemoryMoveLog : IMoveLog
{
    public List<MoveLogEntry> Entries { get; } = new();
    public Task AppendAsync(MoveLogEntry entry) { Entries.Add(entry); return Task.CompletedTask; }
    public Task<IReadOnlyList<MoveLogEntry>> ReadAllAsync() => Task.FromResult<IReadOnlyList<MoveLogEntry>>(Entries.ToList());
}
