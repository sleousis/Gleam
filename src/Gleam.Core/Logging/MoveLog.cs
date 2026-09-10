using System.Text.Json;
using System.Text.Json.Serialization;
using Gleam.Core.Model;

namespace Gleam.Core.Logging;

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

/// <summary>
/// JSON-lines log of any record type, one line per entry, used by both histories.
///
/// Each entry is appended as one line. The whole file used to be rewritten for every item cleaned or moved,
/// so each action cost more than the one before it. A line cut short by a crash is skipped on reading, and
/// the next entry starts on a line of its own, so a torn line never takes its neighbour with it.
/// </summary>
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
    /// <summary>Whether the file on disk ends at a line break, so an appended line starts on a line of its own.</summary>
    private bool endsCleanly = true;

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
            var line = JsonSerializer.Serialize(entry, JsonOptions) + "\n";
            await storage.AppendAsync(path, endsCleanly ? line : "\n" + line).ConfigureAwait(false);
            endsCleanly = true;
            all.Add(entry);
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

    /// <summary>Empties the file and what is held of it.</summary>
    public async Task ClearAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await storage.WriteAsync(path, string.Empty).ConfigureAwait(false);
            cache = new List<T>();
            endsCleanly = true;
        }
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
        endsCleanly = string.IsNullOrEmpty(text) || text.EndsWith('\n');
        if (string.IsNullOrWhiteSpace(text)) return cache = loaded;
        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var e = JsonSerializer.Deserialize<T>(line, JsonOptions);
                if (e is not null) loaded.Add(e);
            }
            catch (Exception e) when (e is JsonException or NotSupportedException)
            {
                // A corrupt line, or one of a kind a newer version wrote, must never take the whole history with it.
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
