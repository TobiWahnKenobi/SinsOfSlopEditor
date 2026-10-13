using BCnEncoder.Encoder;
using BCnEncoder.Shared;

namespace SinsEditor.Core.Textures;

/// <summary>Texture file formats the editor writes; existing atlases keep theirs.</summary>
public enum TextureFileFormat
{
    /// <summary>Uncompressed 32-bit BGRA, bottom-left origin: what the game's own TGA icons use.</summary>
    Tga32,
    Tga32Rle,
    Tga24,
    Tga24Rle,
    DdsDxt1,
    DdsDxt3,
    DdsDxt5,
    DdsBgra,
}

public static class TextureFormats
{
    public static bool IsDds(TextureFileFormat f) => f >= TextureFileFormat.DdsDxt1;
    public static string Extension(TextureFileFormat f) => IsDds(f) ? ".dds" : ".tga";
    public static bool HasAlpha(TextureFileFormat f) => f is not (TextureFileFormat.Tga24 or TextureFileFormat.Tga24Rle or TextureFileFormat.DdsDxt1);

    /// <summary>Reads the format from a file's header; null if it isn't a TGA or DDS the editor can write back.</summary>
    public static TextureFileFormat? Detect(string path)
    {
        using var stream = File.OpenRead(path);
        var header = new byte[128];
        int read = stream.Read(header, 0, header.Length);
        if (read >= 128 && header[0] == 'D' && header[1] == 'D' && header[2] == 'S' && header[3] == ' ')
        {
            bool fourCc = (BitConverter.ToUInt32(header, 80) & 4) != 0;
            string code = System.Text.Encoding.ASCII.GetString(header, 84, 4);
            return fourCc
                ? code switch { "DXT1" => TextureFileFormat.DdsDxt1, "DXT3" => TextureFileFormat.DdsDxt3, "DXT5" => TextureFileFormat.DdsDxt5, _ => null }
                : TextureFileFormat.DdsBgra;
        }
        if (read >= 18 && path.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
        {
            byte type = header[2], bpp = header[16];
            return (type, bpp) switch
            {
                (2, 32) => TextureFileFormat.Tga32,
                (10, 32) => TextureFileFormat.Tga32Rle,
                (2, 24) => TextureFileFormat.Tga24,
                (10, 24) => TextureFileFormat.Tga24Rle,
                _ => null,
            };
        }
        return null;
    }

    /// <summary>
    /// The format to write an existing atlas in: its own, except that 24-bit TGA becomes 32-bit when the new content
    /// has transparency (24-bit can't store it).
    /// </summary>
    public static TextureFileFormat ForRewrite(TextureFileFormat existing, bool needsAlpha) => (existing, needsAlpha) switch
    {
        (TextureFileFormat.Tga24, true) => TextureFileFormat.Tga32,
        (TextureFileFormat.Tga24Rle, true) => TextureFileFormat.Tga32Rle,
        _ => existing,
    };

    public static bool HasTransparency(PixelImage image)
    {
        for (int i = 3; i < image.Bgra.Length; i += 4)
        {
            if (image.Bgra[i] != 255) return true;
        }
        return false;
    }

    public static byte[] Encode(PixelImage image, TextureFileFormat format) => IsDds(format) ? EncodeDds(image, format) : EncodeTga(image, format);

    /// <summary>TGA with bottom-left origin (rows stored bottom-up), 8 alpha bits for 32-bit, optional RLE.</summary>
    public static byte[] EncodeTga(PixelImage image, TextureFileFormat format)
    {
        bool rle = format is TextureFileFormat.Tga32Rle or TextureFileFormat.Tga24Rle;
        int bytesPerPixel = format is TextureFileFormat.Tga24 or TextureFileFormat.Tga24Rle ? 3 : 4;
        using var output = new MemoryStream();
        var header = new byte[18];
        header[2] = (byte)(rle ? 10 : 2);
        header[12] = (byte)(image.Width & 0xFF);
        header[13] = (byte)(image.Width >> 8);
        header[14] = (byte)(image.Height & 0xFF);
        header[15] = (byte)(image.Height >> 8);
        header[16] = (byte)(bytesPerPixel * 8);
        header[17] = (byte)(bytesPerPixel == 4 ? 8 : 0);
        output.Write(header);

        var pixel = new byte[bytesPerPixel];
        for (int y = image.Height - 1; y >= 0; y--)
        {
            int row = y * image.Width * 4;
            if (!rle)
            {
                for (int x = 0; x < image.Width; x++) output.Write(image.Bgra, row + x * 4, bytesPerPixel);
                continue;
            }
            // RLE packets never cross rows (some readers require that).
            int x0 = 0;
            while (x0 < image.Width)
            {
                int run = 1;
                while (x0 + run < image.Width && run < 128 && SamePixel(image.Bgra, row + x0 * 4, row + (x0 + run) * 4, bytesPerPixel)) run++;
                if (run > 1)
                {
                    output.WriteByte((byte)(0x80 | (run - 1)));
                    output.Write(image.Bgra, row + x0 * 4, bytesPerPixel);
                    x0 += run;
                    continue;
                }
                int raw = 1;
                while (x0 + raw < image.Width && raw < 128
                       && !(x0 + raw + 1 < image.Width && SamePixel(image.Bgra, row + (x0 + raw) * 4, row + (x0 + raw + 1) * 4, bytesPerPixel))) raw++;
                output.WriteByte((byte)(raw - 1));
                for (int i = 0; i < raw; i++) output.Write(image.Bgra, row + (x0 + i) * 4, bytesPerPixel);
                x0 += raw;
            }
        }
        return output.ToArray();
    }

    private static bool SamePixel(byte[] data, int a, int b, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (data[a + i] != data[b + i]) return false;
        }
        return true;
    }

    /// <summary>DDS with a full mip chain and a classic (non-DX10) header, as the game's own textures have.</summary>
    public static byte[] EncodeDds(PixelImage image, TextureFileFormat format)
    {
        var encoder = new BcEncoder();
        encoder.OutputOptions.GenerateMipMaps = true;
        encoder.OutputOptions.Quality = CompressionQuality.BestQuality;
        encoder.OutputOptions.FileFormat = OutputFileFormat.Dds;
        encoder.OutputOptions.DdsPreferDxt10Header = false;
        encoder.OutputOptions.Format = format switch
        {
            TextureFileFormat.DdsDxt1 => CompressionFormat.Bc1,
            TextureFileFormat.DdsDxt3 => CompressionFormat.Bc2,
            TextureFileFormat.DdsDxt5 => CompressionFormat.Bc3,
            _ => CompressionFormat.Bgra,
        };
        var dds = encoder.EncodeToDds(image.Bgra, image.Width, image.Height, PixelFormat.Bgra32);
        using var output = new MemoryStream();
        dds.Write(output);
        return output.ToArray();
    }
}
