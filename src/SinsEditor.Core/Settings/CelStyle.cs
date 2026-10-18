using System.Globalization;
using System.Numerics;

namespace SinsEditor.Core.Settings;

/// <summary>
/// How the mesh view's cel-shading renderer draws (e.g. for HUD icons): the fill (the mesh's texture or one colour),
/// light in a few hard bands, and lines along the outline and creases. Colours are <c>#RRGGBB</c>. Stored by name as
/// presets in the editor settings.
/// </summary>
public sealed record CelStyle
{
    public string Name { get; init; } = "";
    /// <summary>Fill with the mesh's diffuse texture; otherwise with <see cref="FillColor"/>.</summary>
    public bool UseTexture { get; init; } = true;
    /// <summary>Tint the texture's team colour areas (its alpha) with the team colour.</summary>
    public bool TeamColor { get; init; } = true;
    public string FillColor { get; init; } = "#9FB0C8";
    /// <summary>Light levels: 1 is flat, 2 lit/shadow, more gives softer steps.</summary>
    public int Bands { get; init; } = 3;
    /// <summary>How dark the darkest band is, 0 (no shading) to 1 (black).</summary>
    public double Shadow { get; init; } = 0.55;
    public bool Outline { get; init; } = true;
    /// <summary>Also lines where surfaces meet at an angle (creases) and where parts overlap, not only the outline.</summary>
    public bool InnerLines { get; init; } = true;
    public string LineColor { get; init; } = "#101418";
    /// <summary>Line width in pixels of the picture.</summary>
    public double LineWidth { get; init; } = 2;
    /// <summary>Creases sharper than this angle (degrees) get a line.</summary>
    public double CreaseAngle { get; init; } = 40;

    public static CelStyle Default { get; } = new() { Name = "Default" };

    /// <summary>A colour <c>#RRGGBB</c> (or <c>#AARRGGBB</c>) as RGBA 0..1; null if it isn't one.</summary>
    public static Vector4? ParseColor(string text)
    {
        string hex = text.Trim().TrimStart('#');
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return null;
        float a = hex.Length == 8 ? (v >> 24 & 255) / 255f : 1f;
        return new Vector4((v >> 16 & 255) / 255f, (v >> 8 & 255) / 255f, (v & 255) / 255f, a);
    }
}
