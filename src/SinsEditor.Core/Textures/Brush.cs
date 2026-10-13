using SinsEditor.Core.Documents;
using SinsEditor.Core.Index;

namespace SinsEditor.Core.Textures;

/// <summary>One image region of a brush: a texture name and a rectangle in it.</summary>
public sealed record BrushImage(string FileName, PixelBox Box, int LineIndex);

/// <summary>
/// A named image region from a <c>.brushes</c> file. Content types:
/// <c>Simple</c> and <c>Margins</c> have one image; <c>States</c> and <c>MarginsAndStates</c> have one per state
/// (Normal, CursorOver, Pressed, Focused, Disabled).
/// </summary>
public sealed class Brush
{
    public const string SimpleState = "";
    public const string NormalState = "Normal";

    private Brush(NamedEntry entry, string content, IReadOnlyDictionary<string, BrushImage> images)
    {
        Entry = entry;
        Content = content;
        Images = images;
    }

    public NamedEntry Entry { get; }
    public string Name => Entry.Name;
    public string Content { get; }
    /// <summary>Images keyed by state name; <see cref="SimpleState"/> for single-image brushes.</summary>
    public IReadOnlyDictionary<string, BrushImage> Images { get; }

    /// <summary>The image to show as a preview: Normal for state brushes, the only image otherwise.</summary>
    public BrushImage? PreviewImage =>
        Images.GetValueOrDefault(NormalState) ?? Images.GetValueOrDefault(SimpleState) ?? Images.Values.FirstOrDefault();

    public static Brush FromEntry(NamedEntry entry)
    {
        var node = entry.Node;
        string content = node.Child("content") is { } c && SinsValue.TryGetString(c.Value, out var s) ? s : "";
        var images = new Dictionary<string, BrushImage>(StringComparer.OrdinalIgnoreCase);
        if (Read(node) is { } single) images[SimpleState] = single;
        foreach (var child in node.Children)
        {
            if (child.Children.Count > 0 && Read(child) is { } image) images[child.Key] = image;
        }
        return new Brush(entry, content, images);
    }

    private static BrushImage? Read(SinsNode node)
    {
        if (node.Child("fileName") is not { } file || !SinsValue.TryGetString(file.Value, out var fileName)) return null;
        var box = node.Child("pixelBox") is { } b && PixelBox.TryParse(b.Value, out var parsed) ? parsed : default;
        return new BrushImage(fileName, box, file.LineIndex);
    }
}
