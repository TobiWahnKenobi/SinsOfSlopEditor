using SinsEditor.Core.Documents;
using SinsEditor.Core.Editing;
using SinsEditor.Core.Index;

namespace SinsEditor.Core.Validation;

/// <summary>
/// What entities of each type look like, learned from the layers below the edited mod (the base game and base mods),
/// which the game is known to read: the keys each type has, the kind of value each key holds, and which keys count
/// the items after them. The edited mod's entities are checked against that.
/// </summary>
public sealed class EntitySchema
{
    /// <summary>Keys need this many samples before their value kind is trusted.</summary>
    private const int MinSamples = 3;

    private readonly Dictionary<string, int> _typeCounts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<SinsValueKind, int>> _kinds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _counters = new(StringComparer.Ordinal);
    /// <summary>Counters by key alone, for types whose base-game entities never count past one (e.g. a single weapon).</summary>
    private readonly Dictionary<string, string> _countersByKey = new(StringComparer.Ordinal);

    private EntitySchema() { }

    public int LearnedTypes => _typeCounts.Count;

    // By key name, not position: the game ignores indentation, and hand-written mods indent freely.
    private static string Key(string type, string key) => type + "|" + key;

    public static EntitySchema Learn(ModIndex index)
    {
        var schema = new EntitySchema();
        var counterHits = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var counterMisses = new Dictionary<string, int>(StringComparer.Ordinal);
        var counterLongRuns = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entity in index.Entities.Values.Where(e => !e.File.Layer.IsEditable && e.EntityType.Length > 0))
        {
            schema._typeCounts[entity.EntityType] = schema._typeCounts.GetValueOrDefault(entity.EntityType) + 1;
            var doc = entity.Document;
            var lines = doc.Lines;
            var parents = doc.GetParentIndices();
            for (int i = 1; i < lines.Count; i++)
            {
                if (lines[i].IsBlank) continue;
                string key = Key(entity.EntityType, lines[i].Key);
                schema._keys.Add(key);
                if (!schema._kinds.TryGetValue(key, out var kinds)) schema._kinds[key] = kinds = [];
                var kind = lines[i].ValueKind;
                kinds[kind] = kinds.GetValueOrDefault(kind) + 1;

                // A counter: a key named like one, holding a positive integer followed by exactly that many items of one key.
                if (kind != SinsValueKind.Integer || !CountedList.IsCountLike(lines[i].Key) || !SinsValue.TryGetInt(lines[i].Value, out int n) || n <= 0) continue;
                string? itemKey = CountedList.NextItemKey(doc, parents, i);
                if (itemKey != null && itemKey != lines[i].Key && CountedList.RunAfter(doc, parents, i, itemKey).Count == n)
                {
                    if (!counterHits.TryGetValue(key, out var items)) counterHits[key] = items = new(StringComparer.Ordinal);
                    items[itemKey] = items.GetValueOrDefault(itemKey) + 1;
                    if (n >= 2) counterLongRuns.Add(key);
                }
                else
                {
                    counterMisses[key] = counterMisses.GetValueOrDefault(key) + 1;
                }
            }
        }

        // Only keys that always count (a few coincidences aside) and have counted two or more items somewhere are counters;
        // a value that's mostly 1 followed by one line can be chance.
        foreach (var (key, items) in counterHits)
        {
            var (itemKey, hits) = items.MaxBy(kv => kv.Value);
            if (hits >= 2 && counterLongRuns.Contains(key) && counterMisses.GetValueOrDefault(key) * 20 <= hits && hits * 10 >= items.Values.Sum() * 9)
                schema._counters[key] = itemKey;
        }
        foreach (var group in schema._counters.GroupBy(kv => kv.Key[(kv.Key.IndexOf('|') + 1)..]))
        {
            if (group.Select(kv => kv.Value).Distinct().Count() == 1) schema._countersByKey[group.Key] = group.First().Value;
        }
        return schema;
    }

    public bool IsCounter(string entityType, string key) => _counters.ContainsKey(Key(entityType, key));

    /// <summary>The kind of value a key holds in the base game, if it's always the same.</summary>
    public SinsValueKind? ExpectedKind(string entityType, string key) =>
        _kinds.TryGetValue(Key(entityType, key), out var kinds) && kinds.Count == 1 ? kinds.Keys.First()
        : kinds != null && kinds.Count == 2 && kinds.ContainsKey(SinsValueKind.Integer) && kinds.ContainsKey(SinsValueKind.Float) ? SinsValueKind.Float
        : null;

    public IEnumerable<Problem> Check(EntityRecord entity)
    {
        string type = entity.EntityType;
        if (type.Length == 0 || !_typeCounts.ContainsKey(type)) yield break;
        var doc = entity.Document;
        var lines = doc.Lines;
        var parents = doc.GetParentIndices();
        var unknownLines = new HashSet<int>();
        for (int i = 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.IsBlank) continue;

            string key = Key(type, line.Key);

            if (!_keys.Contains(key))
            {
                unknownLines.Add(i);
                // Lines inside an unknown block are covered by the block's problem.
                if (parents[i] < 0 || !unknownLines.Contains(parents[i]))
                {
                    string where = parents[i] >= 0 ? $" in {lines[parents[i]].Key}" : "";
                    // Info only: keys added by newer game versions never appear in the base files.
                    yield return new Problem(ProblemSeverity.Info, ProblemCodes.UnknownProperty,
                        $"{entity.Name}: no {type} below this mod uses '{line.Key}'{where}. If it isn't a newer key, check the spelling: the game stops reading at keys it doesn't expect.",
                        entity.File.RelativePath, i);
                }
                continue;
            }

            var kind = line.ValueKind;
            if (_kinds.TryGetValue(key, out var kinds) && kinds.Values.Sum() >= MinSamples && !kinds.ContainsKey(kind)
                && !(kind == SinsValueKind.Integer && kinds.ContainsKey(SinsValueKind.Float)))
            {
                var expected = kinds.MaxBy(kv => kv.Value).Key;
                yield return new Problem(ProblemSeverity.Error, ProblemCodes.WrongValueKind,
                    $"{entity.Name}: {line.Key} should be {Describe(expected)}, but is {(kind == SinsValueKind.None ? "empty" : $"'{line.Value}'")}.",
                    entity.File.RelativePath, i);
            }

            if ((_counters.TryGetValue(key, out var itemKey) || _countersByKey.TryGetValue(line.Key, out itemKey))
                && SinsValue.TryGetInt(line.Value, out int declared))
            {
                int actual = CountedList.RunAfter(doc, parents, i, itemKey).Count;
                if (actual != declared && LooseCount(doc, i, itemKey) != declared)
                {
                    yield return new Problem(ProblemSeverity.Error, ProblemCodes.CountMismatch,
                        $"{entity.Name}: {line.Key} says {declared}, but {actual} {itemKey} {(actual == 1 ? "entry follows" : "entries follow")}. The game reads exactly {declared}.",
                        entity.File.RelativePath, i);
                }
            }
        }
    }

    /// <summary>
    /// Items counted without trusting indentation (a stray unindented line inside an item breaks the tree, not the game):
    /// lines with the item key at most one level deeper than the counter, up to where the counter's parent ends
    /// or the same counter starts again.
    /// </summary>
    private static int LooseCount(SinsDocument doc, int counter, string itemKey)
    {
        var lines = doc.Lines;
        int depth = lines[counter].Depth, count = 0;
        for (int i = counter + 1; i < lines.Count; i++)
        {
            if (lines[i].IsBlank) continue;
            if (lines[i].Depth < depth || (lines[i].Depth == depth && lines[i].Key == lines[counter].Key)) break;
            if (lines[i].Key == itemKey && lines[i].Depth <= depth + 1) count++;
        }
        return count;
    }

    private static string Describe(SinsValueKind kind) => kind switch
    {
        SinsValueKind.None => "a block without a value",
        SinsValueKind.String => "a quoted text",
        SinsValueKind.Integer => "a whole number",
        SinsValueKind.Float => "a number",
        SinsValueKind.Bool => "TRUE or FALSE",
        SinsValueKind.Vector => "a vector [ … ]",
        _ => "a value like the base game's",
    };
}
