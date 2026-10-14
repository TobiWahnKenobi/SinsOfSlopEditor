using BCnEncoder.Decoder;
using BCnEncoder.Shared;

namespace SinsEditor.Core.Textures;

/// <summary>The six faces of a cube map in DirectX order: +X, −X, +Y, −Y, +Z, −Z.</summary>
public sealed record CubeMap(int Size, IReadOnlyList<PixelImage> Faces);

/// <summary>
/// Reads cube map DDS files like the game's skybox environment maps (DXT1/DXT3/DXT5 or 32-bit BGRA, six faces, only
/// the top mip level is kept).
/// </summary>
public static class CubeMapLoader
{
    private const uint CubeAllFaces = 0xFC00;

    public static CubeMap Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < 128 || BitConverter.ToUInt32(data, 0) != 0x20534444) throw new TextureLoadException($"{Path.GetFileName(path)} isn't a DDS file.");
        int height = BitConverter.ToInt32(data, 12), width = BitConverter.ToInt32(data, 16);
        int mips = Math.Max(1, BitConverter.ToInt32(data, 28));
        uint pfFlags = BitConverter.ToUInt32(data, 80);
        string fourCc = System.Text.Encoding.ASCII.GetString(data, 84, 4);
        int bits = BitConverter.ToInt32(data, 88);
        uint redMask = BitConverter.ToUInt32(data, 92);
        uint caps2 = BitConverter.ToUInt32(data, 112);
        if ((caps2 & CubeAllFaces) != CubeAllFaces) throw new TextureLoadException($"{Path.GetFileName(path)} isn't a cube map.");

        CompressionFormat? block = (pfFlags & 0x4) != 0
            ? fourCc switch { "DXT1" => CompressionFormat.Bc1, "DXT3" => CompressionFormat.Bc2, "DXT5" => CompressionFormat.Bc3, _ => null }
            : null;
        if ((pfFlags & 0x4) != 0 && block is null) throw new TextureLoadException($"Cube maps in {fourCc} aren't supported.");
        if (block is null && bits != 32) throw new TextureLoadException($"Cube maps with {bits}-bit pixels aren't supported.");
        int blockBytes = block == CompressionFormat.Bc1 ? 8 : 16;

        long MipSize(int level)
        {
            int w = Math.Max(1, width >> level), h = Math.Max(1, height >> level);
            return block is null ? (long)w * h * 4 : (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * blockBytes;
        }
        long faceSize = Enumerable.Range(0, mips).Sum(MipSize);

        var faces = new List<PixelImage>(6);
        var decoder = new BcDecoder();
        for (int face = 0; face < 6; face++)
        {
            int offset = 128 + (int)(face * faceSize);
            int top = (int)MipSize(0);
            if (offset + top > data.Length) throw new TextureLoadException($"{Path.GetFileName(path)} is truncated.");
            var bgra = new byte[width * height * 4];
            if (block is { } format)
            {
                var pixels = decoder.DecodeRaw(data.AsSpan(offset, top).ToArray(), width, height, format);
                for (int i = 0; i < pixels.Length; i++)
                {
                    bgra[i * 4] = pixels[i].b;
                    bgra[i * 4 + 1] = pixels[i].g;
                    bgra[i * 4 + 2] = pixels[i].r;
                    bgra[i * 4 + 3] = pixels[i].a;
                }
            }
            else
            {
                Buffer.BlockCopy(data, offset, bgra, 0, top);
                // RGBA in memory (red mask 0xFF) instead of the usual BGRA: swap red and blue.
                if (redMask == 0xFF)
                    for (int i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
            }
            faces.Add(new PixelImage(width, height, bgra));
        }
        return new CubeMap(width, faces);
    }
}
