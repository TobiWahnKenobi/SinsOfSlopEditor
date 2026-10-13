using SinsEditor.Core.Textures;

namespace SinsEditor.Tests.Textures;

public class TextureTests
{
    private static PixelImage Gradient(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int o = (y * w + x) * 4;
            px[o] = (byte)x; px[o + 1] = (byte)y; px[o + 2] = 0; px[o + 3] = 255;
        }
        return new PixelImage(w, h, px);
    }

    [Fact]
    public void Crop_copies_the_rectangle()
    {
        var crop = Gradient(10, 10).Crop(new PixelBox(2, 3, 4, 5));

        Assert.Equal((4, 5), (crop.Width, crop.Height));
        Assert.Equal(2, crop.Bgra[0]);
        Assert.Equal(3, crop.Bgra[1]);
        Assert.Equal(5, crop.Bgra[((4 * 4) + 3) * 4]); // bottom-right: x = 2 + 3
    }

    [Fact]
    public void Crop_outside_the_image_is_transparent()
    {
        var crop = Gradient(4, 4).Crop(new PixelBox(2, 2, 4, 4));

        Assert.Equal(255, crop.Bgra[3]);                // (0,0) → source (2,2)
        Assert.Equal(0, crop.Bgra[(3 * 4 + 3) * 4 + 3]); // (3,3) → source (5,5), outside
    }

    [Theory]
    [InlineData("[ 0 , 33 , 47 , 33 ]", true)]
    [InlineData("[ 1 , 2 , 3 ]", false)]
    public void PixelBox_parses_four_values(string raw, bool ok)
    {
        Assert.Equal(ok, PixelBox.TryParse(raw, out var box));
        if (ok) Assert.Equal(new PixelBox(0, 33, 47, 33), box);
    }

    [Fact]
    public void Png_has_signature_and_chunks()
    {
        byte[] png = PngWriter.Encode(Gradient(3, 2));

        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal("IEND", System.Text.Encoding.ASCII.GetString(png, png.Length - 8, 4));
    }
}
