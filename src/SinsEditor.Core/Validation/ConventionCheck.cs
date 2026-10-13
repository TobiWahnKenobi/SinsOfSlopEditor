using System.Text;
using SinsEditor.Core.Documents;
using SinsEditor.Core.FileSystem;

namespace SinsEditor.Core.Validation;

/// <summary>A text file in the edited mod whose encoding or line endings differ from the base game's convention.</summary>
public sealed record ConventionMismatch(string RelativePath, string AbsolutePath, TextConvention Expected, string Reason);

/// <summary>Finds and fixes files in the edited mod that don't follow <see cref="TextConventions"/>.</summary>
public static class ConventionCheck
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static IReadOnlyList<ConventionMismatch> Find(LayerStack stack)
    {
        if (stack.EditedMod is not { } mod) return [];
        var result = new List<ConventionMismatch>();
        foreach (var (relative, absolute) in mod.Files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            byte[] bytes = File.ReadAllBytes(absolute);
            if (SinsFileFormatDetector.Detect(bytes) != SinsFileFormat.Text) continue;

            // Scans bytes instead of parsing: text meshes in a mod can have millions of lines.
            var scan = Scan(bytes);
            var expected = ExpectedConvention(stack, relative, scan.HasNonAscii);
            string? reason = Describe(scan, expected);
            if (reason != null) result.Add(new ConventionMismatch(relative, absolute, expected, reason));
        }
        return result;
    }

    /// <summary>Encoding and line endings a file in the edited mod should have, following the base game's version of it.</summary>
    public static TextConvention ExpectedConvention(LayerStack stack, string relativePath, bool hasNonAsciiText) =>
        TextConventions.Resolve(relativePath, GameEncoding(stack, relativePath), hasNonAsciiText);

    /// <summary>Rewrites the files with the expected encoding and line endings. Returns how many were changed.</summary>
    public static int Fix(IEnumerable<ConventionMismatch> mismatches)
    {
        int fixedCount = 0;
        foreach (var m in mismatches)
        {
            var doc = SinsDocument.Load(m.AbsolutePath);
            if (doc.ApplyConvention(m.Expected))
            {
                doc.Save(m.AbsolutePath);
                fixedCount++;
            }
        }
        return fixedCount;
    }

    public static Problem ToProblem(IReadOnlyList<ConventionMismatch> mismatches) => new(
        ProblemSeverity.Warning, ProblemCodes.ConventionMismatch,
        $"{mismatches.Count} files don't match the game's encoding and line endings.");

    private readonly record struct ByteScan(SinsTextEncoding Encoding, bool HasNonAscii, bool HasNonCrlfEndings);

    private static ByteScan Scan(byte[] bytes)
    {
        bool bom = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]);
        var body = bom ? bytes.AsSpan(3) : bytes.AsSpan();
        bool nonAscii = false, nonCrlf = false;
        for (int i = 0; i < body.Length; i++)
        {
            byte b = body[i];
            if (b >= 0x80) nonAscii = true;
            else if (b == '\n' && (i == 0 || body[i - 1] != '\r')) nonCrlf = true;
            else if (b == '\r' && (i + 1 >= body.Length || body[i + 1] != '\n')) nonCrlf = true;
        }
        var encoding = bom ? SinsTextEncoding.Utf8WithBom
            : !nonAscii || IsValidUtf8(body) ? SinsTextEncoding.Utf8
            : SinsTextEncoding.Ansi;
        return new ByteScan(encoding, nonAscii, nonCrlf);
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            StrictUtf8.GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string? Describe(ByteScan scan, TextConvention expected)
    {
        var parts = new List<string>();
        if (scan.Encoding != expected.Encoding) parts.Add($"{Name(scan.Encoding)} instead of {Name(expected.Encoding)}");
        if (scan.HasNonCrlfEndings) parts.Add("LF line endings instead of CRLF");
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    private static string Name(SinsTextEncoding encoding) => encoding switch
    {
        SinsTextEncoding.Utf8WithBom => "UTF-8 with BOM",
        SinsTextEncoding.Ansi => "Windows-1252",
        _ => "UTF-8 without BOM",
    };

    /// <summary>Encoding of the base game's version of a file, UTF-8 with BOM if it's binary, or null if the game doesn't have it.</summary>
    private static SinsTextEncoding? GameEncoding(LayerStack stack, string relative)
    {
        if (stack.BaseGame?.GetAbsolutePath(Languages.GameCounterpart(relative)) is not { } gamePath) return null;
        return SinsFileFormatDetector.Detect(gamePath) switch
        {
            SinsFileFormat.Binary => SinsTextEncoding.Utf8WithBom,
            SinsFileFormat.Text => Scan(File.ReadAllBytes(gamePath)).Encoding,
            _ => null,
        };
    }
}
