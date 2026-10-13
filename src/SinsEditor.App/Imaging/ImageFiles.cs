using System.Windows.Media;
using System.Windows.Media.Imaging;
using SinsEditor.Core.Textures;

namespace SinsEditor.App.Imaging;

/// <summary>Loads images from disk for import: PNG/JPG/BMP/GIF/TIFF through WPF, TGA and DDS through the game texture reader.</summary>
public static class ImageFiles
{
    public const string DialogFilter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.tga;*.dds|All files|*.*";

    public static PixelImage Load(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".tga" or ".dds") return TextureLoader.Load(path);

        var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if (frame.Format != PixelFormats.Bgra32) frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int stride = frame.PixelWidth * 4;
        var pixels = new byte[stride * frame.PixelHeight];
        frame.CopyPixels(pixels, stride, 0);
        return new PixelImage(frame.PixelWidth, frame.PixelHeight, pixels);
    }
}
