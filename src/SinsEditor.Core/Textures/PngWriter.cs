using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;

namespace SinsEditor.Core.Textures;

/// <summary>Minimal PNG encoder (RGBA, no filtering) for exporting previews without a UI framework.</summary>
public static class PngWriter
{
    public static void Save(PixelImage image, string path) => File.WriteAllBytes(path, Encode(image));

    public static byte[] Encode(PixelImage image)
    {
        using var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        WriteChunk(output, "IHDR", header);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + image.Width * 4];
            for (int y = 0; y < image.Height; y++)
            {
                row[0] = 0; // filter: none
                for (int x = 0; x < image.Width; x++)
                {
                    int s = (y * image.Width + x) * 4, d = 1 + x * 4;
                    row[d] = image.Bgra[s + 2];
                    row[d + 1] = image.Bgra[s + 1];
                    row[d + 2] = image.Bgra[s];
                    row[d + 3] = image.Bgra[s + 3];
                }
                z.Write(row);
            }
        }
        WriteChunk(output, "IDAT", raw.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = new Crc32();
        crc.Append(typeBytes);
        crc.Append(data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc.GetCurrentHashAsUInt32());
        output.Write(crcBytes);
    }
}
