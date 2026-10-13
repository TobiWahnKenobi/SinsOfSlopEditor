using Pfim;

namespace SinsEditor.Core.Textures;

public sealed class TextureLoadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Decodes <c>.dds</c> and <c>.tga</c> textures (via Pfim) into BGRA pixels. Only the top mip level is used.</summary>
public static class TextureLoader
{
    public static PixelImage Load(string path)
    {
        IImage image;
        try
        {
            image = Pfimage.FromFile(path);
        }
        catch (Exception e)
        {
            throw new TextureLoadException($"Couldn't read '{Path.GetFileName(path)}': {e.Message}", e);
        }

        using (image)
        {
            return Convert(image, Path.GetFileName(path));
        }
    }

    private static PixelImage Convert(IImage image, string name)
    {
        int w = image.Width, h = image.Height, stride = image.Stride;
        byte[] src = image.Data;
        var dst = new byte[w * h * 4];

        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                switch (image.Format)
                {
                    // Pfim stores 32 and 24 bit data in BGRA/BGR byte order.
                    case ImageFormat.Rgba32:
                        Buffer.BlockCopy(src, row + x * 4, dst, o, 4);
                        break;
                    case ImageFormat.Rgb24:
                        dst[o] = src[row + x * 3];
                        dst[o + 1] = src[row + x * 3 + 1];
                        dst[o + 2] = src[row + x * 3 + 2];
                        dst[o + 3] = 255;
                        break;
                    case ImageFormat.Rgb8:
                        dst[o] = dst[o + 1] = dst[o + 2] = src[row + x];
                        dst[o + 3] = 255;
                        break;
                    case ImageFormat.R5g6b5:
                    {
                        int v = src[row + x * 2] | src[row + x * 2 + 1] << 8;
                        Set(dst, o, Expand5(v >> 11), Expand6(v >> 5), Expand5(v), 255);
                        break;
                    }
                    case ImageFormat.R5g5b5:
                    {
                        int v = src[row + x * 2] | src[row + x * 2 + 1] << 8;
                        Set(dst, o, Expand5(v >> 10), Expand5(v >> 5), Expand5(v), 255);
                        break;
                    }
                    case ImageFormat.R5g5b5a1:
                    {
                        int v = src[row + x * 2] | src[row + x * 2 + 1] << 8;
                        Set(dst, o, Expand5(v >> 10), Expand5(v >> 5), Expand5(v), (v & 0x8000) != 0 ? 255 : 0);
                        break;
                    }
                    case ImageFormat.Rgba16:
                    {
                        int v = src[row + x * 2] | src[row + x * 2 + 1] << 8;
                        Set(dst, o, ((v >> 8) & 15) * 17, ((v >> 4) & 15) * 17, (v & 15) * 17, ((v >> 12) & 15) * 17);
                        break;
                    }
                    default:
                        throw new TextureLoadException($"'{name}' uses pixel format {image.Format}, which isn't supported.");
                }
            }
        }
        return new PixelImage(w, h, dst);
    }

    private static int Expand5(int v) => (v & 31) * 255 / 31;
    private static int Expand6(int v) => (v & 63) * 255 / 63;

    private static void Set(byte[] dst, int o, int r, int g, int b, int a)
    {
        dst[o] = (byte)b;
        dst[o + 1] = (byte)g;
        dst[o + 2] = (byte)r;
        dst[o + 3] = (byte)a;
    }
}
