using System.Text;
using SinsEditor.Core.Documents;

namespace SinsEditor.Core.Index;

/// <summary>A property entities can be filtered by. An entity can have several values (e.g. owned by two races) or none.</summary>
public sealed class Facet(string id, string title, Func<EntityRecord, IReadOnlyList<string>> values)
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public IReadOnlyList<string> ValuesOf(EntityRecord entity) => values(entity);
}

public static class Facets
{
    public const string RaceId = "#race";
    public const string LayerId = "#layer";
    public const string TypeId = "#type";
    public const string ProblemsId = "#problems";
    public const string HasProblems = "Has problems";
    public const string NoProblems = "No problems";

    /// <summary>
    /// Facets for a set of entities: race, layer, type (if mixed) and problems, plus up to <paramref name="maxAuto"/>
    /// properties found in the data. A top-level property qualifies when it isn't a reference or a count, appears in at least
    /// half of the entities and has between 2 and 30 distinct values. Repeated properties (like <c>frigateRoleType</c>,
    /// a list after <c>typeCount</c>) give an entity several values.
    /// </summary>
    public static IReadOnlyList<Facet> For(ModIndex index, IReadOnlyCollection<EntityRecord> entities,
        Func<EntityRecord, bool> hasProblems, int maxAuto = 5)
    {
        var facets = new List<Facet>
        {
            new(RaceId, "Race", e => index.RacesOwning(e.Name).Select(r => index.DisplayName(r) ?? r.Name).ToList()),
            new(LayerId, "Layer", e => [e.File.Layer.Name]),
        };
        if (entities.Select(e => e.EntityType).Distinct().Count() > 1)
            facets.Add(new Facet(TypeId, "Type", e => [e.EntityType]));
        facets.Add(new Facet(ProblemsId, "Problems", e => [hasProblems(e) ? HasProblems : NoProblems]));

        foreach (string key in FindPropertyFacets(index, entities).Take(maxAuto))
            facets.Add(new Facet(key, Humanize(key), e => TopLevelValues(e, key)));
        return facets;
    }

    /// <summary>
    /// Top-level property keys usable as facets, best first: names that read like a classification (…Type, …Field, Tier),
    /// then text before numbers and booleans, then how evenly entities spread over the values (a property where one value
    /// covers nearly everything doesn't filter much).
    /// </summary>
    public static IEnumerable<string> FindPropertyFacets(ModIndex index, IReadOnlyCollection<EntityRecord> entities)
    {
        if (entities.Count < 2) return [];
        var stats = new Dictionary<string, KeyStats>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in TopLevelLines(entity))
            {
                if (line.Key == "entityType" || IsCountLike(line.Key) || index.ReferenceKeys.ContainsKey("/" + line.Key)) continue;
                if (Display(line.Value) is not { } value) continue;
                if (!stats.TryGetValue(line.Key, out var s)) stats[line.Key] = s = new KeyStats();
                if (seen.Add(line.Key)) s.Count++;
                if (SinsValue.Classify(line.Value) == SinsValueKind.String) s.TextValues++;
                s.Values[value] = s.Values.GetValueOrDefault(value) + 1;
            }
        }
        return stats
            .Where(kv => kv.Value.Count * 2 >= entities.Count && kv.Value.Values.Count is >= 2 and <= 30)
            .OrderByDescending(kv => LooksLikeClassification(kv.Key))
            .ThenByDescending(kv => kv.Value.TextValues > 0)
            .ThenByDescending(kv => kv.Value.Spread)
            .Select(kv => kv.Key);
    }

    private sealed class KeyStats
    {
        public int Count;
        public int TextValues;
        public readonly Dictionary<string, int> Values = new(StringComparer.Ordinal);

        /// <summary>0 when one value covers every occurrence, approaching 1 when values are evenly spread.</summary>
        public double Spread => 1.0 - (double)Values.Values.Max() / Values.Values.Sum();
    }

    public static IReadOnlyList<string> TopLevelValues(EntityRecord entity, string key) =>
        TopLevelLines(entity).Where(l => l.Key == key).Select(l => Display(l.Value)).OfType<string>().Distinct().ToList();

    /// <summary>Counters and per-slot keys (<c>NumWeapons</c>, <c>MeshNameInfoCount</c>, <c>NumSoundsFor:ONCREATION</c>) are structure, not properties.</summary>
    private static bool IsCountLike(string key) =>
        key.StartsWith("num", StringComparison.OrdinalIgnoreCase) || key.EndsWith("Count", StringComparison.OrdinalIgnoreCase) || key.Contains(':');

    private static readonly string[] ClassificationSuffixes = ["Type", "Field", "Role", "Class", "Tier", "Category"];

    private static bool LooksLikeClassification(string key) =>
        ClassificationSuffixes.Any(s => key.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>"frigateRoleType" → "Frigate role type".</summary>
    public static string Humanize(string key)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < key.Length; i++)
        {
            char c = key[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(key[i - 1])) sb.Append(' ');
            sb.Append(sb.Length == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static IEnumerable<SinsLine> TopLevelLines(EntityRecord entity) =>
        entity.Document.Lines.Skip(1).Where(l => !l.IsBlank && l.Depth == 0);

    /// <summary>Strings without quotes, numbers and booleans as written; other values don't make good facets.</summary>
    private static string? Display(string raw) => SinsValue.Classify(raw) switch
    {
        SinsValueKind.String => SinsValue.TryGetString(raw, out var s) && s.Length > 0 ? s : null,
        SinsValueKind.Integer or SinsValueKind.Bool => raw,
        _ => null,
    };
}
