using SinsEditor.Core.Documents;
using SinsEditor.Core.FileSystem;

namespace SinsEditor.Core.Index;

/// <summary>A named entry inside a file, e.g. a brush in a <c>.brushes</c> file or a sound effect in a <c>.sounddata</c> file.</summary>
public sealed record NamedEntry(string Name, ResolvedFile File, SinsNode Node);

/// <summary>
/// Entries found as <c>&lt;section&gt;</c> blocks with a <c>name "…"</c> child across several files,
/// such as <c>brush</c> in <c>.brushes</c> and <c>effect</c> in <c>.sounddata</c>. First definition wins, like the game.
/// </summary>
public sealed class NamedEntryCatalog
{
    private readonly Dictionary<string, NamedEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<NamedEntry> _duplicates = [];

    public IReadOnlyDictionary<string, NamedEntry> Entries => _entries;
    public IReadOnlyList<NamedEntry> Duplicates => _duplicates;

    public bool Contains(string name) => _entries.ContainsKey(name);

    public void AddFrom(ResolvedFile file, SinsDocument document, string sectionKey)
    {
        foreach (var node in document.BuildTree().Children.Where(n => n.Key == sectionKey))
        {
            if (node.Child("name") is not { } nameNode || !SinsValue.TryGetString(nameNode.Value, out var name)) continue;
            var entry = new NamedEntry(name, file, node);
            if (!_entries.TryAdd(name, entry)) _duplicates.Add(entry);
        }
    }
}
