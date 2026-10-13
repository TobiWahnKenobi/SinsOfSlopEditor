using SinsEditor.Core.Conversion;

namespace SinsEditor.Core.Documents;

/// <summary>Encoding and line ending a file should be written with.</summary>
public sealed record TextConvention(SinsTextEncoding Encoding, string LineEnding = "\r\n");

/// <summary>
/// Matches files written by the editor to the base game's conventions:
/// <list type="bullet">
/// <item>Line endings are always CRLF (every text file in the game uses CRLF).</item>
/// <item>Encoding follows the same file in the base game. Binary game files count as UTF-8 with BOM,
/// which is what ConvertData writes when converting to text.</item>
/// <item>Files without a base-game counterpart: UTF-8 with BOM for ConvertData types
/// (<c>.entity</c>, <c>.mesh</c>, <c>.particle</c>, <c>.brushes</c>), plain UTF-8 otherwise.</item>
/// <item>Non-ASCII content always gets a BOM, as the game's localized <c>.str</c> files have,
/// so the game doesn't read it as Windows-1252.</item>
/// </list>
/// </summary>
public static class TextConventions
{
    public const string LineEnding = "\r\n";

    /// <param name="relativePath">Path inside the mod, used for the file type.</param>
    /// <param name="baseGameEncoding">
    /// Encoding of the same file in the base game, <see cref="SinsTextEncoding.Utf8WithBom"/> if the game's file is binary,
    /// or null if the game doesn't have it.
    /// </param>
    /// <param name="hasNonAsciiText">Whether the document contains characters outside ASCII.</param>
    public static TextConvention Resolve(string relativePath, SinsTextEncoding? baseGameEncoding, bool hasNonAsciiText)
    {
        var encoding = baseGameEncoding
            ?? (ConvertDataTool.CanConvert(relativePath) ? SinsTextEncoding.Utf8WithBom : SinsTextEncoding.Utf8);
        if (encoding != SinsTextEncoding.Utf8WithBom && hasNonAsciiText) encoding = SinsTextEncoding.Utf8WithBom;
        return new TextConvention(encoding, LineEnding);
    }

    public static TextConvention Resolve(string relativePath, SinsTextEncoding? baseGameEncoding, SinsDocument document) =>
        Resolve(relativePath, baseGameEncoding, document.HasNonAsciiText);
}
