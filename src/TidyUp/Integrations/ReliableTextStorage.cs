using Dalamud.Plugin.Services;
using TidyUp.Core.Logging;

namespace TidyUp.Integrations;

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
}
