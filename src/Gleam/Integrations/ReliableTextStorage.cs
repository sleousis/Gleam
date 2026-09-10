using Dalamud.Plugin.Services;
using Gleam.Core.Logging;

namespace Gleam.Integrations;

/// <summary>Core's <see cref="ITextStorage"/> over Dalamud's crash-safe file storage, rooted in the plugin config folder.</summary>
public sealed class ReliableTextStorage : ITextStorage
{
    private readonly IReliableFileStorage storage;
    private readonly string root;

    public ReliableTextStorage(IReliableFileStorage storage, string root)
    {
        this.storage = storage;
        this.root = root;
        Directory.CreateDirectory(root);
    }

    private string Full(string path) => Path.Combine(root, path);

    public bool Exists(string path) => storage.Exists(Full(path));

    public async Task<string?> ReadAsync(string path)
    {
        try { return await storage.ReadAllTextAsync(Full(path)).ConfigureAwait(false); }
        catch (FileNotFoundException) { return null; }
    }

    public Task WriteAsync(string path, string contents) => storage.WriteAllTextAsync(Full(path), contents);

    /// <summary>
    /// Adds to the end of the file itself. Dalamud's storage only writes whole files, and a history that grows
    /// by a line per item cost more to rewrite with every action. A file Dalamud holds only in its backup is
    /// written out whole once first, so an append never starts a file without its past.
    /// </summary>
    public async Task AppendAsync(string path, string text)
    {
        var full = Full(path);
        if (!File.Exists(full))
        {
            var existing = storage.Exists(full) ? await ReadAsync(path).ConfigureAwait(false) : null;
            await storage.WriteAllTextAsync(full, (existing ?? string.Empty) + text).ConfigureAwait(false);
            return;
        }
        await File.AppendAllTextAsync(full, text).ConfigureAwait(false);
    }
}
