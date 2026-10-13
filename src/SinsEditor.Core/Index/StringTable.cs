using SinsEditor.Core.Documents;
using SinsEditor.Core.FileSystem;

namespace SinsEditor.Core.Index;

/// <summary>One <c>StringInfo</c> block. <paramref name="Ordinal"/> is its position among all blocks of the file.</summary>
public sealed record StringEntry(string Id, string Value, int IdLineIndex, int Ordinal);

public enum DuplicateKind
{
    /// <summary>Consecutive blocks with the same ID and different text, e.g. the game's IDS_TAUNT_ lines: the game picks one. Not a problem.</summary>
    Variants,
    /// <summary>The same ID and the same text more than once; the extra copies can be removed.</summary>
    IdenticalCopies,
    /// <summary>The same ID defined again elsewhere in the file with different text; probably a mistake.</summary>
    Scattered,
}

public sealed record DuplicateGroup(string Id, IReadOnlyList<StringEntry> Entries, DuplicateKind Kind);

/// <summary>
/// A <c>.str</c> file: <c>StringInfo</c> blocks with <c>ID "…"</c> and <c>Value "…"</c>. IDs compare case-insensitively.
/// An ID may appear several times: the base game lists AI taunt variants that way.
/// </summary>
public sealed class StringTable
{
    private readonly Dictionary<string, List<StringEntry>> _entries = new(StringComparer.OrdinalIgnoreCase);

    private StringTable(string language, ResolvedFile? file, SinsDocument? document)
    {
        Language = language;
        File = file;
        Document = document;
    }

    public string Language { get; }
    public ResolvedFile? File { get; }
    public SinsDocument? Document { get; }
    /// <summary>Number of <c>StringInfo</c> blocks, counting repeated IDs.</summary>
    public int TotalCount { get; private set; }
    public IReadOnlyCollection<string> Ids => _entries.Keys;

    /// <summary>The <c>NumStrings</c> header value, to compare against <see cref="TotalCount"/>.</summary>
    public int? DeclaredCount { get; private set; }

    /// <summary>First definition of every ID.</summary>
    public IEnumerable<StringEntry> Entries => _entries.Values.Select(list => list[0]);

    public IEnumerable<DuplicateGroup> Duplicates => _entries.Values
        .Where(list => list.Count > 1)
        .Select(list => new DuplicateGroup(list[0].Id, list, Classify(list)));

    public static StringTable Empty(string language) => new(language, null, null);

    public static StringTable Load(string language, ResolvedFile file, SinsDocument document)
    {
        var table = new StringTable(language, file, document);
        string? pendingId = null;
        int pendingLine = -1;
        for (int i = 0; i < document.Lines.Count; i++)
        {
            var line = document.Lines[i];
            switch (line.Key)
            {
                case "NumStrings" when SinsValue.TryGetInt(line.Value, out int n):
                    table.DeclaredCount = n;
                    break;
                case "ID" when SinsValue.TryGetString(line.Value, out var id):
                    pendingId = id;
                    pendingLine = i;
                    break;
                case "Value" when pendingId != null:
                    // Values may contain characters that break the simple quote check, so strip quotes leniently.
                    string raw = line.Value;
                    string value = raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' ? raw[1..^1] : raw;
                    if (!table._entries.TryGetValue(pendingId, out var list)) table._entries[pendingId] = list = [];
                    list.Add(new StringEntry(pendingId, value, pendingLine, table.TotalCount++));
                    pendingId = null;
                    break;
            }
        }
        return table;
    }

    public bool Contains(string id) => _entries.ContainsKey(id);

    /// <summary>Text of an ID (the first variant if there are several).</summary>
    public string? Get(string id) => _entries.TryGetValue(id, out var list) ? list[0].Value : null;

    public IReadOnlyList<StringEntry> AllOf(string id) => _entries.TryGetValue(id, out var list) ? list : [];

    private static DuplicateKind Classify(List<StringEntry> list)
    {
        if (list.Select(e => e.Value).Distinct(StringComparer.Ordinal).Count() < list.Count) return DuplicateKind.IdenticalCopies;
        bool consecutive = list.Zip(list.Skip(1)).All(p => p.Second.Ordinal == p.First.Ordinal + 1);
        return consecutive ? DuplicateKind.Variants : DuplicateKind.Scattered;
    }
}
