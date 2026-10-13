namespace SinsEditor.Core.Textures;

/// <summary>How an imported image is brought to the brush's size.</summary>
public enum FitMode
{
    /// <summary>Scale to exactly the target size, ignoring the aspect ratio.</summary>
    Stretch,
    /// <summary>Keep the aspect ratio and fit inside; the rest stays transparent.</summary>
    Fit,
    /// <summary>Keep the aspect ratio and fill the target; the overflow is cut off evenly.</summary>
    Fill,
}

/// <summary>The button states of state brushes, as the game's files name them.</summary>
public static class BrushStates
{
    public const string Normal = "Normal";
    public const string CursorOver = "CursorOver";
    public const string Pressed = "Pressed";
    public const string Focused = "Focused";
    public const string Disabled = "Disabled";

    /// <summary>File order used by the game's own brushes.</summary>
    public static readonly IReadOnlyList<string> All = [Disabled, Pressed, CursorOver, Focused, Normal];
}

public static class ImageImport
{
    public static PixelImage Resize(PixelImage source, int width, int height, FitMode mode)
    {
        if (mode == FitMode.Stretch || source.Width == 0 || source.Height == 0) return ImageOps.Scale(source, width, height);
        double sx = (double)width / source.Width, sy = (double)height / source.Height;
        double scale = mode == FitMode.Fit ? Math.Min(sx, sy) : Math.Max(sx, sy);
        int w = Math.Max(1, (int)Math.Round(source.Width * scale)), h = Math.Max(1, (int)Math.Round(source.Height * scale));
        var scaled = ImageOps.Scale(source, w, h);
        var result = new byte[width * height * 4];
        int ox = (width - w) / 2, oy = (height - h) / 2;
        for (int y = 0; y < height; y++)
        {
            int syy = y - oy;
            if (syy < 0 || syy >= h) continue;
            for (int x = 0; x < width; x++)
            {
                int sxx = x - ox;
                if (sxx < 0 || sxx >= w) continue;
                Buffer.BlockCopy(scaled.Bgra, (syy * w + sxx) * 4, result, (y * width + x) * 4, 4);
            }
        }
        return new PixelImage(width, height, result);
    }

    /// <summary>
    /// The other button states from Normal, in the style of the game's icon sheets: CursorOver brighter, Pressed darker,
    /// Disabled desaturated and dimmed, Focused like Normal.
    /// </summary>
    public static PixelImage DeriveState(PixelImage normal, string state) => state switch
    {
        BrushStates.CursorOver => Adjust(normal, brightness: 1.25, saturation: 1.0),
        BrushStates.Pressed => Adjust(normal, brightness: 0.75, saturation: 1.0),
        BrushStates.Disabled => Adjust(normal, brightness: 0.55, saturation: 0.0),
        _ => normal,
    };

    private static PixelImage Adjust(PixelImage source, double brightness, double saturation)
    {
        var dst = (byte[])source.Bgra.Clone();
        for (int i = 0; i < dst.Length; i += 4)
        {
            double b = dst[i], g = dst[i + 1], r = dst[i + 2];
            double gray = 0.114 * b + 0.587 * g + 0.299 * r;
            dst[i] = Clamp((gray + (b - gray) * saturation) * brightness);
            dst[i + 1] = Clamp((gray + (g - gray) * saturation) * brightness);
            dst[i + 2] = Clamp((gray + (r - gray) * saturation) * brightness);
        }
        return new PixelImage(source.Width, source.Height, dst);
    }

    private static byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    /// <summary>A content hash, so identical images can share one atlas region.</summary>
    public static string Hash(PixelImage image) =>
        $"{image.Width}x{image.Height}:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image.Bgra));
}
