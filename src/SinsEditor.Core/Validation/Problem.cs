namespace SinsEditor.Core.Validation;

public enum ProblemSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>Something wrong with the mod. <paramref name="Code"/> groups problems of the same kind on the dashboard.</summary>
public sealed record Problem(
    ProblemSeverity Severity,
    string Code,
    string Message,
    string? RelativePath = null,
    int? LineIndex = null);

public static class ProblemCodes
{
    public const string ManifestEntryWithoutFile = "manifest-entry-without-file";
    public const string EntityNotInManifest = "entity-not-in-manifest";
    public const string MissingReference = "missing-reference";
    public const string MissingString = "missing-string";
    public const string DuplicateString = "duplicate-string";
    public const string ScatteredString = "scattered-string";
    public const string StringCountMismatch = "string-count-mismatch";
    public const string DuplicateBrush = "duplicate-brush";
    public const string MissingBrushFile = "missing-brush-file";
    public const string ConversionFailed = "conversion-failed";
    public const string MissingManifest = "missing-manifest";
    public const string ConventionMismatch = "convention-mismatch";
    public const string BrushImageProblem = "brush-image";

    /// <summary>Short title for a group of problems on the dashboard.</summary>
    public static string Title(string code) => code switch
    {
        ManifestEntryWithoutFile => "Manifest entry without file",
        EntityNotInManifest => "Entity not in manifest",
        MissingReference => "Missing reference",
        MissingString => "Missing string",
        DuplicateString => "Duplicate string",
        ScatteredString => "String defined in several places",
        StringCountMismatch => "String count mismatch",
        DuplicateBrush => "Duplicate brush",
        MissingBrushFile => "Missing brush file",
        ConversionFailed => "Conversion failed",
        MissingManifest => "Missing manifest",
        ConventionMismatch => "Encoding or line endings",
        BrushImageProblem => "Brush image outside texture",
        _ => code,
    };
}
