namespace SinsEditor.Core.Textures;

/// <summary>Scaling helpers for rendering brushes at the size the game draws them.</summary>
public static class ImageOps
{
    /// <summary>Bilinear scaling to an exact size.</summary>
    public static PixelImage Scale(PixelImage source, int width, int height)
    {
        if (source.Width == width && source.Height == height) return source;
        var dst = new byte[width * height * 4];
        double sx = (double)source.Width / width, sy = (double)source.Height / height;
        for (int y = 0; y < height; y++)
        {
            double fy = Math.Max(0, (y + 0.5) * sy - 0.5);
            int y0 = Math.Min((int)fy, source.Height - 1), y1 = Math.Min(y0 + 1, source.Height - 1);
            double ty = fy - y0;
            for (int x = 0; x < width; x++)
            {
                double fx = Math.Max(0, (x + 0.5) * sx - 0.5);
                int x0 = Math.Min((int)fx, source.Width - 1), x1 = Math.Min(x0 + 1, source.Width - 1);
                double tx = fx - x0;
                int o = (y * width + x) * 4;
                for (int c = 0; c < 4; c++)
                {
                    double top = Px(source, x0, y0, c) * (1 - tx) + Px(source, x1, y0, c) * tx;
                    double bottom = Px(source, x0, y1, c) * (1 - tx) + Px(source, x1, y1, c) * tx;
                    dst[o + c] = (byte)Math.Round(top * (1 - ty) + bottom * ty);
                }
            }
        }
        return new PixelImage(width, height, dst);
    }

    /// <summary>
    /// Nine-slice: the four corners keep their size, edges stretch along one axis, the centre along both.
    /// Margins larger than the target are clamped.
    /// </summary>
    public static PixelImage NineSlice(PixelImage source, Margins m, int width, int height)
    {
        int l = Math.Min(m.Left, Math.Min(source.Width, width) / 2), r = Math.Min(m.Right, Math.Min(source.Width, width) / 2);
        int t = Math.Min(m.Top, Math.Min(source.Height, height) / 2), b = Math.Min(m.Bottom, Math.Min(source.Height, height) / 2);
        int[] srcX = [0, l, source.Width - r, source.Width];
        int[] srcY = [0, t, source.Height - b, source.Height];
        int[] dstX = [0, l, width - r, width];
        int[] dstY = [0, t, height - b, height];
        var dst = new byte[width * height * 4];
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                var piece = new PixelBox(srcX[col], srcY[row], srcX[col + 1] - srcX[col], srcY[row + 1] - srcY[row]);
                int w = dstX[col + 1] - dstX[col], h = dstY[row + 1] - dstY[row];
                if (piece.Width <= 0 || piece.Height <= 0 || w <= 0 || h <= 0) continue;
                var scaled = Scale(source.Crop(piece), w, h);
                for (int y = 0; y < h; y++)
                    Buffer.BlockCopy(scaled.Bgra, y * w * 4, dst, ((dstY[row] + y) * width + dstX[col]) * 4, w * 4);
            }
        }
        return new PixelImage(width, height, dst);
    }

    private static byte Px(PixelImage image, int x, int y, int channel) => image.Bgra[(y * image.Width + x) * 4 + channel];
}
