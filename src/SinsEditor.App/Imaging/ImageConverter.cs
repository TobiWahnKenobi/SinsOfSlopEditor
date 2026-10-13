using System.Windows.Media;
using System.Windows.Media.Imaging;
using SinsEditor.Core.Textures;

namespace SinsEditor.App.Imaging;

public static class ImageConverter
{
    public static BitmapSource ToBitmap(PixelImage image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Bgra, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Renders a brush as a frozen bitmap, or null if it can't be shown.</summary>
    public static BitmapSource? RenderBrush(TextureStore store, string? brushName)
    {
        if (string.IsNullOrEmpty(brushName)) return null;
        var result = store.RenderBrush(brushName);
        return result.Image is { Width: > 0, Height: > 0 } image ? ToBitmap(image) : null;
    }
}
