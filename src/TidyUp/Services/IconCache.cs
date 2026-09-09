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
    private ISharedImmediateTexture? logoMedium;
    private ISharedImmediateTexture? logoSmall;

    /// <param name="imagesDir">Folder holding icon.png (512), icon-192.png and icon-96.png. The game samples textures
    /// without mipmaps, so each on-screen size gets an image downsampled offline at roughly twice its pixel size.</param>
    public IconCache(ITextureProvider textures, string? imagesDir = null)
    {
        this.textures = textures;
        if (imagesDir is null) return;
        logo = Load(Path.Combine(imagesDir, "icon.png"));
        logoMedium = Load(Path.Combine(imagesDir, "icon-192.png")) ?? logo;
        logoSmall = Load(Path.Combine(imagesDir, "icon-96.png")) ?? logoMedium;
    }

    private ISharedImmediateTexture? Load(string path) => File.Exists(path) ? textures.GetFromFile(path) : null;

    public ImTextureID Get(uint iconId, bool hq)
    {
        if (iconId == 0) return ImTextureID.Null;
        if (!lookups.TryGetValue((iconId, hq), out var tex))
            lookups[(iconId, hq)] = tex = textures.GetFromGameIcon(new GameIconLookup(iconId, hq));
        return tex.GetWrapOrEmpty().Handle;
    }

    /// <summary>The plugin logo at full size, or a null handle when the image is missing.</summary>
    public ImTextureID Logo => logo?.GetWrapOrDefault()?.Handle ?? ImTextureID.Null;

    /// <summary>For the 72 px empty-state and running screens.</summary>
    public ImTextureID LogoMedium => logoMedium?.GetWrapOrDefault()?.Handle ?? Logo;

    /// <summary>For the 36 px window header: thicker strokes, fewer sparkles.</summary>
    public ImTextureID LogoSmall => logoSmall?.GetWrapOrDefault()?.Handle ?? LogoMedium;
}
