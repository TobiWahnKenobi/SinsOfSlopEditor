using SinsEditor.Core.Documents;

namespace SinsEditor.Core.Index;

/// <summary>
/// What the data says about a property (by qualified key <c>parentKey/key</c>): the kind of value it usually holds,
/// values seen elsewhere (for text properties with few distinct values), and for references what they point to.
/// </summary>
public sealed record PropertyHint(
    string Context,
    SinsValueKind Kind,
    IReadOnlyList<string> KnownValues,
    ReferenceKind? Reference,
    IReadOnlyList<string> TargetEntityTypes);

public sealed class PropertyHints
{
    private const int MaxKnownValues = 40;
    private readonly Dictionary<string, PropertyHint> _hints = new(StringComparer.Ordinal);

    private PropertyHints() { }

    public PropertyHint? Get(string context) => _hints.GetValueOrDefault(context);

    public static PropertyHints Build(ModIndex index)
    {
        var kinds = new Dictionary<string, Dictionary<SinsValueKind, int>>(StringComparer.Ordinal);
        var values = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var entity in index.ActiveEntities)
        {
            var lines = entity.Document.Lines;
            var parents = entity.Document.GetParentIndices();
            for (int i = 1; i < lines.Count; i++)
            {
                if (lines[i].IsBlank || !lines[i].HasValue) continue;
                string context = ContextOf(lines, parents, i);
                var kind = lines[i].ValueKind;
                if (!kinds.TryGetValue(context, out var counts)) kinds[context] = counts = [];
                counts[kind] = counts.GetValueOrDefault(kind) + 1;
                if (kind == SinsValueKind.String && SinsValue.TryGetString(lines[i].Value, out var s) && s.Length > 0)
                {
                    if (!values.TryGetValue(context, out var set)) values[context] = set = new HashSet<string>(StringComparer.Ordinal);
                    if (set.Count <= MaxKnownValues) set.Add(s);
                }
            }
        }

        var targetTypes = index.References
            .Where(r => r.Kind == ReferenceKind.Entity && r.Resolved)
            .GroupBy(r => r.Context, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => index.Entities[r.Target].EntityType).Distinct().Order().ToList(),
                StringComparer.Ordinal);

        var hints = new PropertyHints();
        foreach (var (context, counts) in kinds)
        {
            var kind = counts.MaxBy(kv => kv.Value).Key;
            // Mixed integers and floats are floats.
            if (kind == SinsValueKind.Integer && counts.ContainsKey(SinsValueKind.Float)) kind = SinsValueKind.Float;
            ReferenceKind? reference = index.ReferenceKeys.TryGetValue(context, out var r) ? r : null;
            IReadOnlyList<string> known = reference is null && values.TryGetValue(context, out var set) && set.Count is > 1 and <= MaxKnownValues
                ? set.Order(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
            hints._hints[context] = new PropertyHint(context, kind, known, reference, targetTypes.GetValueOrDefault(context) ?? []);
        }
        return hints;
    }

    public static string ContextOf(List<SinsLine> lines, int[] parents, int i) =>
        (parents[i] >= 0 ? lines[parents[i]].Key : "") + "/" + lines[i].Key;
}
