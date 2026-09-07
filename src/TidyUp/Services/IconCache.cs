using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;

namespace TidyUp.Services;

/// <summary>Item icons for ImGui. Dalamud caches the textures; this only remembers the lookups.</summary>
public sealed class IconCache
{
    private readonly ITextureProvider textures;
    private readonly Dictionary<(uint, bool), ISharedImmediateTexture> lookups = new();

    public IconCache(ITextureProvider textures)
    {
        this.textures = textures;
    }

    public ImTextureID Get(uint iconId, bool hq)
    {
        if (iconId == 0) return ImTextureID.Null;
        if (!lookups.TryGetValue((iconId, hq), out var tex))
            lookups[(iconId, hq)] = tex = textures.GetFromGameIcon(new GameIconLookup(iconId, hq));
        return tex.GetWrapOrEmpty().Handle;
    }
}
