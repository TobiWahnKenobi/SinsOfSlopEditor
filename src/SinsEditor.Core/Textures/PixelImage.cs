namespace SinsEditor.Core.Textures;

/// <summary>A decoded image as 32-bit BGRA pixels (the layout WPF's <c>Bgra32</c> expects), rows top to bottom.</summary>
public sealed class PixelImage
{
    public PixelImage(int width, int height, byte[] bgra)
    {
        if (bgra.Length != width * height * 4) throw new ArgumentException("Pixel buffer size doesn't match the dimensions.");
        Width = width;
        Height = height;
        Bgra = bgra;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }
    public int Stride => Width * 4;

    /// <summary>Whether a rectangle lies fully inside the image.</summary>
    public bool Contains(PixelBox box) =>
        box.X >= 0 && box.Y >= 0 && box.Width > 0 && box.Height > 0 && box.X + box.Width <= Width && box.Y + box.Height <= Height;

    /// <summary>Copies a rectangle out of the image. Parts outside the image are left transparent.</summary>
    public PixelImage Crop(PixelBox box)
    {
        int w = Math.Max(0, box.Width), h = Math.Max(0, box.Height);
        var result = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int sy = box.Y + y;
            if (sy < 0 || sy >= Height) continue;
            int x0 = Math.Max(0, box.X), x1 = Math.Min(Width, box.X + w);
            if (x1 <= x0) continue;
            Buffer.BlockCopy(Bgra, (sy * Width + x0) * 4, result, (y * w + (x0 - box.X)) * 4, (x1 - x0) * 4);
        }
        return new PixelImage(w, h, result);
    }
}

/// <summary>A rectangle in texture pixels, as in <c>pixelBox [ x , y , width , height ]</c>.</summary>
public readonly record struct PixelBox(int X, int Y, int Width, int Height)
{
    public static bool TryParse(string raw, out PixelBox box)
    {
        box = default;
        if (!Documents.SinsValue.TryGetVector(raw, out var v) || v.Length != 4) return false;
        box = new PixelBox((int)v[0], (int)v[1], (int)v[2], (int)v[3]);
        return true;
    }
}
