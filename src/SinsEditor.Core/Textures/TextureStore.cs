using System.Collections.Concurrent;
using SinsEditor.Core.Index;

namespace SinsEditor.Core.Textures;

/// <summary>
/// Result of rendering a brush. <paramref name="Error"/> describes a problem; the image may still be set (cropped as far
/// as possible). <paramref name="IsEmpty"/> marks placeholder brushes with <c>pixelBox [0, 0, 0, 0]</c>, which draw nothing.
/// </summary>
public sealed record BrushRenderResult(PixelImage? Image, string? Error, bool IsEmpty = false);

/// <summary>
/// Loads textures of the effective mod and cuts brush images out of them. Decoded textures are cached
/// (brushes usually share large atlas textures); the cache holds at most <see cref="Capacity"/> textures.
/// </summary>
public sealed class TextureStore(ModIndex index)
{
    private readonly ConcurrentDictionary<string, Lazy<PixelImage?>> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _order = new();

    public int Capacity { get; init; } = 64;

    /// <summary>Decoded texture by name as written in the files (with or without .dds/.tga), or null with a reason.</summary>
    public PixelImage? GetTexture(string name, out string? error)
    {
        string key = TextureKey(name);
        if (!index.Textures.TryGetValue(key, out var file))
        {
            error = $"Texture '{name}' doesn't exist.";
            return null;
        }

        var lazy = _textures.GetOrAdd(key, k =>
        {
            _order.Enqueue(k);
            return new Lazy<PixelImage?>(() =>
            {
                try
                {
                    return TextureLoader.Load(file.AbsolutePath);
                }
                catch (TextureLoadException e)
                {
                    _errors[k] = e.Message;
                    return null;
                }
            });
        });
        var image = lazy.Value;
        Trim();
        error = image is null ? _errors.GetValueOrDefault(key) : null;
        return image;
    }

    public BrushRenderResult RenderBrush(string brushName, string? state = null)
    {
        if (!index.Brushes.Entries.TryGetValue(brushName, out var entry))
            return new BrushRenderResult(null, $"Brush '{brushName}' doesn't exist.");
        var brush = Brush.FromEntry(entry);
        var image = state is null ? brush.PreviewImage : brush.Images.GetValueOrDefault(state);
        if (image is null) return new BrushRenderResult(null, $"Brush '{brushName}' has no image{(state is null ? "" : $" for state {state}")}.");

        if (image.Box.Width == 0 && image.Box.Height == 0) return new BrushRenderResult(null, null, IsEmpty: true);

        var texture = GetTexture(image.FileName, out var error);
        if (texture is null) return new BrushRenderResult(null, error);
        if (!texture.Contains(Shrink(image.Box, OverhangTolerance)))
            return new BrushRenderResult(texture.Crop(image.Box),
                $"Brush '{brushName}': pixelBox {image.Box} is outside texture '{image.FileName}' ({texture.Width}×{texture.Height}).");
        return new BrushRenderResult(texture.Crop(image.Box), null);
    }

    /// <summary>
    /// Renders a brush at a given size. Margins brushes are drawn as nine-slice (corners fixed, edges and centre
    /// stretched); other brushes are scaled. Returns null if the brush can't be rendered.
    /// </summary>
    public PixelImage? RenderBrushSized(string brushName, int width, int height)
    {
        if (width <= 0 || height <= 0 || !index.Brushes.Entries.TryGetValue(brushName, out var entry)) return null;
        var brush = Brush.FromEntry(entry);
        var source = RenderBrush(brushName).Image;
        if (source is null || source.Width == 0) return null;
        return brush.StretchMargins.IsEmpty ? ImageOps.Scale(source, width, height) : ImageOps.NineSlice(source, brush.StretchMargins, width, height);
    }

    /// <summary>The game's own data overhangs texture edges by one pixel (e.g. 412 + 101 on a 512 texture) without visible effect.</summary>
    private const int OverhangTolerance = 1;

    private static PixelBox Shrink(PixelBox box, int by) =>
        new(box.X, box.Y, Math.Max(1, box.Width - by), Math.Max(1, box.Height - by));

    private static string TextureKey(string name)
    {
        foreach (string ext in (string[])[".dds", ".tga"])
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return name[..^ext.Length];
        }
        return name;
    }

    private void Trim()
    {
        while (_textures.Count > Capacity && _order.TryDequeue(out var oldest)) _textures.TryRemove(oldest, out _);
    }
}
