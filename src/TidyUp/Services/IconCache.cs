using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;

namespace TidyUp.Services;

/// <summary>Item icons and the plugin logo for ImGui. Dalamud caches the textures; this only remembers the lookups.</summary>
public sealed class IconCache
{
    private readonly ITextureProvider textures;
    private readonly Dictionary<(uint, bool), ISharedImmediateTexture> lookups = new();
    private ISharedImmediateTexture? logo;

    public IconCache(ITextureProvider textures, string? logoPath = null)
    {
        this.textures = textures;
        if (logoPath is not null && File.Exists(logoPath)) logo = textures.GetFromFile(logoPath);
    }

    public ImTextureID Get(uint iconId, bool hq)
    {
        if (iconId == 0) return ImTextureID.Null;
        if (!lookups.TryGetValue((iconId, hq), out var tex))
            lookups[(iconId, hq)] = tex = textures.GetFromGameIcon(new GameIconLookup(iconId, hq));
        return tex.GetWrapOrEmpty().Handle;
    }

    /// <summary>The plugin logo, or a null handle when the image is missing.</summary>
    public ImTextureID Logo => logo?.GetWrapOrDefault()?.Handle ?? ImTextureID.Null;
}
