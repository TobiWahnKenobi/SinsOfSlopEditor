using System.Text.RegularExpressions;
using SinsEditor.Core.Documents;
using SinsEditor.Core.Editing;

namespace SinsEditor.Core.Validation;

/// <summary>
/// Repairs values of the wrong kind where the intent is unambiguous: quoted booleans (<c>"TRUE"</c>, <c>FALSE"</c>)
/// and decimal commas from a German or French locale (<c>0,500000</c>). Anything else needs a person.
/// </summary>
public static partial class ValueFixes
{
    [GeneratedRegex("""^"?(TRUE|FALSE)"?$""", RegexOptions.IgnoreCase)]
    private static partial Regex QuotedBool();

    [GeneratedRegex(@"^-?\d*,\d+$")]
    private static partial Regex CommaDecimal();

    public static string? Fix(string raw, SinsValueKind expected)
    {
        if (expected == SinsValueKind.Bool && QuotedBool().Match(raw) is { Success: true } b) return b.Groups[1].Value.ToUpperInvariant();
        if (expected == SinsValueKind.Float && CommaDecimal().IsMatch(raw)) return raw.Replace(',', '.');
        return null;
    }

    /// <summary>The wrong-kind problems that <see cref="FixAllAsync"/> would repair.</summary>
    public static IReadOnlyList<(Problem Problem, string Entity, string Value)> Fixable(ModWorkspace workspace)
    {
        var result = new List<(Problem, string, string)>();
        var entities = workspace.Index.ActiveEntities.Where(e => e.File.Layer.IsEditable)
            .ToDictionary(e => e.File.RelativePath, StringComparer.OrdinalIgnoreCase);
        foreach (var problem in workspace.Problems.Where(p => p.Code == ProblemCodes.WrongValueKind))
        {
            if (problem.RelativePath is null || problem.LineIndex is not { } i || !entities.TryGetValue(problem.RelativePath, out var entity)) continue;
            var line = entity.Document.Lines[i];
            if (workspace.Schema.ExpectedKind(entity.EntityType, line.Key) is { } kind && Fix(line.Value, kind) is { } value)
                result.Add((problem, entity.Name, value));
        }
        return result;
    }

    /// <summary>
    /// Applies every fixable value through the entities' edit sessions and saves them in their current format.
    /// Entities with unsaved edits are left alone. Returns (fixed values, skipped entities).
    /// </summary>
    public static async Task<(int Fixed, int Skipped)> FixAllAsync(ModWorkspace workspace, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        int done = 0, skipped = 0, entities = 0;
        var groups = Fixable(workspace).GroupBy(f => f.Entity, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var group in groups)
        {
            progress?.Report((double)entities++ / groups.Count);
            if (workspace.ExistingSession(group.Key) is { IsDirty: true })
            {
                skipped++;
                continue;
            }
            var entity = workspace.Index.Entities[group.Key];
            var session = workspace.SessionFor(entity);
            var splices = group.Select(f =>
            {
                var line = EntityEditSession.Clone(session.Document.Lines[f.Problem.LineIndex!.Value]);
                line.SetRawValue(f.Value);
                return new Splice(f.Problem.LineIndex!.Value, 1, [line]);
            }).ToList();
            session.Apply("Fix values", splices);
            await workspace.SaveAsync(session, workspace.DefaultSaveFormat(entity), ct);
            done += splices.Count;
        }
        return (done, skipped);
    }
}
