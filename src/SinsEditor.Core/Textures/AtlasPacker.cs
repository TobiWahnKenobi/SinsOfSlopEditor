namespace SinsEditor.Core.Textures;

public sealed record PackItem(string Key, int Width, int Height);

public sealed record PackResult(int Width, int Height, IReadOnlyDictionary<string, PixelBox> Boxes);

/// <summary>
/// MaxRects bin packing (best short side fit) into the smallest power-of-two texture that fits.
/// Every image gets <c>padding</c> pixels of border on each side and starts on a multiple of <c>align</c>,
/// so neighbours don't bleed into each other when scaled, compressed or mip-mapped.
/// Fixed regions (images that must not move) are kept free.
/// </summary>
public static class AtlasPacker
{
    public static PackResult? Pack(IReadOnlyList<PackItem> items, int padding, int align, IReadOnlyList<PixelBox> fixedRegions,
        int minWidth = 1, int minHeight = 1, int maxSize = 4096, bool powerOfTwo = true)
    {
        long area = items.Sum(i => (long)Padded(i.Width, padding, align) * Padded(i.Height, padding, align))
                    + fixedRegions.Sum(r => (long)r.Width * r.Height);
        int needW = Math.Max(minWidth, fixedRegions.Select(r => r.X + r.Width).DefaultIfEmpty(1).Max());
        int needH = Math.Max(minHeight, fixedRegions.Select(r => r.Y + r.Height).DefaultIfEmpty(1).Max());
        foreach (var item in items)
        {
            needW = Math.Max(needW, Padded(item.Width, padding, align));
            needH = Math.Max(needH, Padded(item.Height, padding, align));
        }

        // Candidate sizes, smallest area first; squares and 2:1 shapes as the game's atlases use.
        var sizes = new List<(int W, int H)>();
        for (int w = Round(needW, powerOfTwo); w <= maxSize; w = powerOfTwo ? w * 2 : w + 64)
        {
            for (int h = Round(needH, powerOfTwo); h <= maxSize; h = powerOfTwo ? h * 2 : h + 64)
            {
                if ((long)w * h >= area) sizes.Add((w, h));
            }
        }
        foreach (var (w, h) in sizes.OrderBy(s => (long)s.W * s.H).ThenBy(s => Math.Abs(s.W - s.H)))
        {
            if (TryPack(items, padding, align, fixedRegions, w, h) is { } boxes) return new PackResult(w, h, boxes);
        }
        return null;
    }

    private static int Round(int value, bool powerOfTwo)
    {
        if (!powerOfTwo) return value;
        int p = 1;
        while (p < value) p *= 2;
        return p;
    }

    private static int Padded(int size, int padding, int align)
    {
        int padded = size + 2 * padding;
        return (padded + align - 1) / align * align;
    }

    private static Dictionary<string, PixelBox>? TryPack(IReadOnlyList<PackItem> items, int padding, int align,
        IReadOnlyList<PixelBox> fixedRegions, int width, int height)
    {
        var free = new List<PixelBox> { new(0, 0, width, height) };
        foreach (var region in fixedRegions) Occupy(free, region);

        var boxes = new Dictionary<string, PixelBox>();
        // Big items first places best.
        foreach (var item in items.OrderByDescending(i => Math.Max(i.Width, i.Height)).ThenByDescending(i => i.Width * i.Height))
        {
            int w = Padded(item.Width, padding, align), h = Padded(item.Height, padding, align);
            PixelBox? best = null;
            int bestShort = int.MaxValue, bestLong = int.MaxValue;
            foreach (var f in free)
            {
                // Align the slot's origin; the aligned slot must still fit inside the free rectangle.
                int ax = (f.X + align - 1) / align * align, ay = (f.Y + align - 1) / align * align;
                if (ax + w > f.X + f.Width || ay + h > f.Y + f.Height) continue;
                int leftH = f.Width - w, leftV = f.Height - h;
                int shortSide = Math.Min(leftH, leftV), longSide = Math.Max(leftH, leftV);
                if (shortSide < bestShort || (shortSide == bestShort && longSide < bestLong))
                {
                    best = new PixelBox(ax, ay, w, h);
                    bestShort = shortSide;
                    bestLong = longSide;
                }
            }
            if (best is not { } slot) return null;
            Occupy(free, slot);
            boxes[item.Key] = new PixelBox(slot.X + padding, slot.Y + padding, item.Width, item.Height);
        }
        return boxes;
    }

    /// <summary>Splits every free rectangle that overlaps <paramref name="used"/> and drops contained ones.</summary>
    private static void Occupy(List<PixelBox> free, PixelBox used)
    {
        var result = new List<PixelBox>();
        foreach (var f in free)
        {
            if (!Overlaps(f, used))
            {
                result.Add(f);
                continue;
            }
            if (used.X > f.X) result.Add(new PixelBox(f.X, f.Y, used.X - f.X, f.Height));
            if (used.X + used.Width < f.X + f.Width) result.Add(new PixelBox(used.X + used.Width, f.Y, f.X + f.Width - used.X - used.Width, f.Height));
            if (used.Y > f.Y) result.Add(new PixelBox(f.X, f.Y, f.Width, used.Y - f.Y));
            if (used.Y + used.Height < f.Y + f.Height) result.Add(new PixelBox(f.X, used.Y + used.Height, f.Width, f.Y + f.Height - used.Y - used.Height));
        }
        // Drop empty rectangles and those inside another one (of two identical ones, keep the first).
        free.Clear();
        for (int i = 0; i < result.Count; i++)
        {
            var r = result[i];
            if (r.Width <= 0 || r.Height <= 0) continue;
            bool redundant = false;
            for (int j = 0; j < result.Count && !redundant; j++)
            {
                if (j != i && Contains(result[j], r) && (result[j] != r || j < i)) redundant = true;
            }
            if (!redundant) free.Add(r);
        }
    }

    private static bool Overlaps(PixelBox a, PixelBox b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    private static bool Contains(PixelBox outer, PixelBox inner) =>
        inner.X >= outer.X && inner.Y >= outer.Y && inner.X + inner.Width <= outer.X + outer.Width && inner.Y + inner.Height <= outer.Y + outer.Height;

    /// <summary>
    /// Draws images into an atlas and repeats each image's edge pixels into its padding, so filtering at the edges samples
    /// the image itself instead of a neighbour. <paramref name="background"/> (e.g. the old atlas when only adding) is copied first.
    /// </summary>
    public static PixelImage Compose(int width, int height, PixelImage? background, IEnumerable<(PixelImage Image, PixelBox Box)> placements, int padding)
    {
        var dst = new byte[width * height * 4];
        if (background != null)
        {
            int w = Math.Min(width, background.Width);
            for (int y = 0; y < Math.Min(height, background.Height); y++)
                Buffer.BlockCopy(background.Bgra, y * background.Width * 4, dst, y * width * 4, w * 4);
        }
        foreach (var (image, box) in placements)
        {
            for (int y = -padding; y < box.Height + padding; y++)
            {
                int ty = box.Y + y;
                if (ty < 0 || ty >= height) continue;
                int sy = Math.Clamp(y, 0, image.Height - 1);
                for (int x = -padding; x < box.Width + padding; x++)
                {
                    int tx = box.X + x;
                    if (tx < 0 || tx >= width) continue;
                    int sx = Math.Clamp(x, 0, image.Width - 1);
                    Buffer.BlockCopy(image.Bgra, (sy * image.Width + sx) * 4, dst, (ty * width + tx) * 4, 4);
                }
            }
        }
        return new PixelImage(width, height, dst);
    }
}
