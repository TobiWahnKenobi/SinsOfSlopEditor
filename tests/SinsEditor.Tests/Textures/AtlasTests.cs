using SinsEditor.Core.Textures;

namespace SinsEditor.Tests.Textures;

public sealed class AtlasTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SinsEditorTests", "atlas-" + Guid.NewGuid().ToString("N"));

    public AtlasTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static PixelImage Pattern(int w, int h, byte alpha = 255)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            px[o] = (byte)(x * 7);
            px[o + 1] = (byte)(y * 5);
            px[o + 2] = (byte)((x + y) % 2 == 0 ? 200 : 40);
            px[o + 3] = x == 0 ? alpha : (byte)255;
        }
        return new PixelImage(w, h, px);
    }

    private static bool Overlaps(PixelBox a, PixelBox b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    private static PixelBox Grow(PixelBox b, int by) => new(b.X - by, b.Y - by, b.Width + 2 * by, b.Height + 2 * by);

    [Fact]
    public void Packs_without_overlap_inside_a_power_of_two_texture()
    {
        var items = Enumerable.Range(0, 40).Select(i => new PackItem($"i{i}", 47 + i % 3, 33 + i % 5)).ToList();

        var result = AtlasPacker.Pack(items, padding: 2, align: 1, fixedRegions: [])!;

        Assert.True(System.Numerics.BitOperations.IsPow2(result.Width) && System.Numerics.BitOperations.IsPow2(result.Height));
        Assert.Equal(items.Count, result.Boxes.Count);
        var boxes = result.Boxes.Values.ToList();
        Assert.All(boxes, b => Assert.True(b.X >= 2 && b.Y >= 2 && b.X + b.Width + 2 <= result.Width && b.Y + b.Height + 2 <= result.Height));
        // With their 2 px padding, no two images touch.
        for (int i = 0; i < boxes.Count; i++)
        for (int j = i + 1; j < boxes.Count; j++)
            Assert.False(Overlaps(Grow(boxes[i], 2), boxes[j]), $"{boxes[i]} and {boxes[j]} too close");
        // 40 icons of about 50×38 fit into 512×256 (one step above their raw area).
        Assert.True((long)result.Width * result.Height <= 512 * 256);
    }

    [Fact]
    public void Aligns_regions_for_dds()
    {
        var items = Enumerable.Range(0, 10).Select(i => new PackItem($"i{i}", 30, 30)).ToList();

        var result = AtlasPacker.Pack(items, padding: 4, align: 4, fixedRegions: [])!;

        Assert.All(result.Boxes.Values, b => Assert.Equal(0, (b.X - 4) % 4));
        Assert.All(result.Boxes.Values, b => Assert.Equal(0, (b.Y - 4) % 4));
    }

    [Fact]
    public void Keeps_fixed_regions_free_and_the_texture_at_least_as_big()
    {
        PixelBox[] fixedRegions = [new(0, 0, 200, 100), new(300, 0, 100, 200)];
        var items = Enumerable.Range(0, 8).Select(i => new PackItem($"i{i}", 40, 40)).ToList();

        var result = AtlasPacker.Pack(items, 2, 1, fixedRegions, minWidth: 512, minHeight: 256)!;

        Assert.True(result.Width >= 512 && result.Height >= 256);
        Assert.All(result.Boxes.Values, b => Assert.DoesNotContain(fixedRegions, f => Overlaps(Grow(b, 2), f)));
    }

    [Fact]
    public void Returns_null_when_nothing_fits()
    {
        Assert.Null(AtlasPacker.Pack([new PackItem("huge", 5000, 10)], 2, 1, [], maxSize: 4096));
    }

    [Fact]
    public void Compose_extrudes_edges_into_the_padding()
    {
        var img = Pattern(4, 4);
        var atlas = AtlasPacker.Compose(16, 16, null, [(img, new PixelBox(4, 4, 4, 4))], padding: 2);

        byte[] Pixel(PixelImage i, int x, int y) => i.Bgra.AsSpan((y * i.Width + x) * 4, 4).ToArray();
        Assert.Equal(Pixel(img, 0, 0), Pixel(atlas, 4, 4));
        Assert.Equal(Pixel(img, 0, 0), Pixel(atlas, 2, 2));   // corner padding repeats the corner
        Assert.Equal(Pixel(img, 3, 1), Pixel(atlas, 9, 5));   // right padding repeats the right edge
        Assert.Equal(new byte[4], Pixel(atlas, 0, 0));        // outside the padding stays empty
    }

    [Theory]
    [InlineData(TextureFileFormat.Tga32)]
    [InlineData(TextureFileFormat.Tga32Rle)]
    [InlineData(TextureFileFormat.DdsBgra)]
    public void Lossless_formats_round_trip(TextureFileFormat format)
    {
        var image = Pattern(37, 21, alpha: 128);
        string path = Path.Combine(_dir, "t" + TextureFormats.Extension(format));

        File.WriteAllBytes(path, TextureFormats.Encode(image, format));

        Assert.Equal(format, TextureFormats.Detect(path));
        Assert.Equal(image.Bgra, TextureLoader.Load(path).Bgra);
    }

    [Fact]
    public void Tga24_drops_alpha()
    {
        var image = Pattern(8, 8, alpha: 0);
        string path = Path.Combine(_dir, "t24.tga");

        File.WriteAllBytes(path, TextureFormats.Encode(image, TextureFileFormat.Tga24));

        var loaded = TextureLoader.Load(path);
        Assert.Equal(TextureFileFormat.Tga24, TextureFormats.Detect(path));
        Assert.Equal(255, loaded.Bgra[3]);
        Assert.Equal(image.Bgra[0], loaded.Bgra[0]);
    }

    [Fact]
    public void Dxt5_writes_a_readable_dds_close_to_the_source()
    {
        var image = Pattern(64, 32);
        string path = Path.Combine(_dir, "t.dds");

        File.WriteAllBytes(path, TextureFormats.Encode(image, TextureFileFormat.DdsDxt5));

        Assert.Equal(TextureFileFormat.DdsDxt5, TextureFormats.Detect(path));
        var loaded = TextureLoader.Load(path);
        Assert.Equal((64, 32), (loaded.Width, loaded.Height));
        double error = image.Bgra.Zip(loaded.Bgra, (a, b) => Math.Abs(a - b)).Average();
        Assert.True(error < 12, $"average error {error}");
    }

    [Fact]
    public void Rewrite_upgrades_24_bit_only_when_transparency_is_needed()
    {
        Assert.Equal(TextureFileFormat.Tga24, TextureFormats.ForRewrite(TextureFileFormat.Tga24, needsAlpha: false));
        Assert.Equal(TextureFileFormat.Tga32, TextureFormats.ForRewrite(TextureFileFormat.Tga24, needsAlpha: true));
        Assert.Equal(TextureFileFormat.DdsDxt5, TextureFormats.ForRewrite(TextureFileFormat.DdsDxt5, needsAlpha: true));
    }

    [Fact]
    public void Fit_letterboxes_and_fill_crops()
    {
        var wide = new PixelImage(100, 50, Enumerable.Repeat((byte)255, 100 * 50 * 4).ToArray());

        var fit = ImageImport.Resize(wide, 40, 40, FitMode.Fit);
        var fill = ImageImport.Resize(wide, 40, 40, FitMode.Fill);

        Assert.Equal(0, fit.Bgra[3]);                       // top row empty: 100×50 fits as 40×20
        Assert.Equal(255, fit.Bgra[(20 * 40 + 20) * 4 + 3]); // middle filled
        Assert.All(Enumerable.Range(0, 40 * 40), i => Assert.Equal(255, fill.Bgra[i * 4 + 3]));
    }

    [Fact]
    public void Derived_states_follow_the_game_style()
    {
        var normal = Pattern(4, 4);

        var disabled = ImageImport.DeriveState(normal, BrushStates.Disabled);
        var over = ImageImport.DeriveState(normal, BrushStates.CursorOver);

        Assert.Equal(disabled.Bgra[0], disabled.Bgra[1]); // grey
        Assert.True(over.Bgra.Sum(b => b) > normal.Bgra.Sum(b => b));
        Assert.Same(normal, ImageImport.DeriveState(normal, BrushStates.Focused));
    }
}
