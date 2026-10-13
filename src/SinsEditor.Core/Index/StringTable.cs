using SinsEditor.Core.Documents;
using SinsEditor.Core.FileSystem;

namespace SinsEditor.Core.Index;

public sealed record StringEntry(string Id, string Value, int IdLineIndex);

/// <summary>
/// A <c>.str</c> file: <c>StringInfo</c> blocks with <c>ID "…"</c> and <c>Value "…"</c>.
/// IDs compare case-insensitively; the first definition wins.
/// </summary>
public sealed class StringTable
{
    private readonly Dictionary<string, StringEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<StringEntry> _duplicates = [];

    private StringTable(string language, ResolvedFile? file, SinsDocument? document)
    {
        Language = language;
        File = file;
        Document = document;
    }

    public string Language { get; }
    public ResolvedFile? File { get; }
    public SinsDocument? Document { get; }
    public IReadOnlyDictionary<string, StringEntry> Entries => _entries;
    public IReadOnlyList<StringEntry> Duplicates => _duplicates;

    /// <summary>The <c>NumStrings</c> header value, to compare against the actual count.</summary>
    public int? DeclaredCount { get; private set; }

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
                    var entry = new StringEntry(pendingId, value, pendingLine);
                    if (!table._entries.TryAdd(pendingId, entry)) table._duplicates.Add(entry);
                    pendingId = null;
                    break;
            }
        }
        return table;
    }

    public bool Contains(string id) => _entries.ContainsKey(id);

    public string? Get(string id) => _entries.TryGetValue(id, out var e) ? e.Value : null;
}
