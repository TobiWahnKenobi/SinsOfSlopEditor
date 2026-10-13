using SinsEditor.Core.Documents;
using SinsEditor.Core.Editing;
using SinsEditor.Core.FileSystem;
using SinsEditor.Core.Index;

namespace SinsEditor.Core.Validation;

/// <summary>A string the mod uses but doesn't define, found in a lower layer (all its variants, in file order).</summary>
public sealed record ImportableString(string Id, IReadOnlyList<string> Values, string LayerName);

public static class StringFixes
{
    /// <summary>
    /// String IDs referenced by the mod's entities but missing from its string table that a lower layer
    /// (base mods, then the base game's localization) defines in the same language.
    /// </summary>
    public static IReadOnlyList<ImportableString> FindImportable(ModWorkspace workspace)
    {
        var index = workspace.Index;
        var missing = index.References
            .Where(r => r.Kind == ReferenceKind.String && !r.Resolved)
            .Select(r => r.Target)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missing.Count == 0) return [];

        // Lower layers only: the effective table already replaces everything above them.
        var effective = index.Strings.File?.Layer;
        var layers = workspace.Stack.Layers.Reverse().ToList();
        if (effective != null) layers = layers.SkipWhile(l => l != effective).Skip(1).ToList();
        var tables = new List<(string Layer, StringTable Table)>();
        foreach (var layer in layers)
        {
            string path = Languages.StringFilePath(layer, index.Language);
            if (layer.GetAbsolutePath(path) is not { } abs) continue;
            tables.Add((layer.Name, StringTable.Load(index.Language, new ResolvedFile(path, layer, abs, []), SinsDocument.Load(abs))));
        }

        var result = new List<ImportableString>();
        foreach (string id in missing)
        {
            foreach (var (layerName, table) in tables)
            {
                var entries = table.AllOf(id);
                if (entries.Count == 0) continue;
                result.Add(new ImportableString(entries[0].Id, entries.Select(e => e.Value).ToList(), layerName));
                break;
            }
        }
        return result;
    }

    /// <summary>
    /// Appends the given strings to the edited mod's string file for the language (copying the effective table first if
    /// it comes from a lower layer) and updates <c>NumStrings</c>. Returns how many <c>StringInfo</c> blocks were added.
    /// </summary>
    public static int ImportStrings(ModWorkspace workspace, IReadOnlyList<ImportableString> strings)
    {
        if (strings.Count == 0) return 0;
        var table = workspace.Index.Strings;
        var mod = workspace.Stack.EditedMod ?? throw new InvalidOperationException("No mod is open for editing.");
        var doc = table.Document is { } d ? SinsDocument.Parse(d.ToBytes()) : SinsDocument.Parse("TXT\r\nNumStrings 0\r\n"u8.ToArray());

        int added = 0;
        foreach (var s in strings)
        {
            foreach (string value in s.Values)
            {
                int end = doc.Lines.Count;
                doc.InsertLine(end, "", "StringInfo");
                doc.InsertLine(end + 1, "\t", "ID", SinsValue.FormatString(s.Id));
                doc.InsertLine(end + 2, "\t", "Value", SinsValue.FormatString(value));
                added++;
            }
        }
        if (doc.Lines.FirstOrDefault(l => l.Key == "NumStrings") is { } count && SinsValue.TryGetInt(count.Value, out int n))
            count.SetRawValue(SinsValue.FormatInt(n + added));

        string relative = Languages.StringFilePath(mod, table.Language);
        doc.ApplyConvention(ConventionCheck.ExpectedConvention(workspace.Stack, relative, doc.HasNonAsciiText));
        SafeWrite.Text(doc, Path.Combine(mod.RootPath, relative.Replace('/', Path.DirectorySeparatorChar)));
        mod.Refresh();
        return added;
    }

    /// <summary>How many <c>StringInfo</c> blocks repeat an ID with text that ID already has.</summary>
    public static int CountIdenticalCopies(StringTable table) =>
        table.Duplicates
            .Where(d => d.Kind == DuplicateKind.IdenticalCopies)
            .Sum(d => d.Entries.Count - d.Entries.Select(e => e.Value).Distinct(StringComparer.Ordinal).Count());

    /// <summary>
    /// Removes repeated <c>StringInfo</c> blocks whose ID and text both appeared earlier, keeping the first of each,
    /// and updates <c>NumStrings</c>. Variants with different text are kept. The result is written to the edited mod's
    /// string file for the language (an override if the table came from a lower layer). Returns how many blocks were removed.
    /// </summary>
    public static int RemoveIdenticalCopies(ModWorkspace workspace)
    {
        var table = workspace.Index.Strings;
        var mod = workspace.Stack.EditedMod ?? throw new InvalidOperationException("No mod is open for editing.");
        if (table.Document is null) return 0;

        var doc = SinsDocument.Parse(table.Document.ToBytes());
        var parents = doc.GetParentIndices();
        var remove = new List<(int Start, int End)>();
        foreach (var group in table.Duplicates.Where(d => d.Kind == DuplicateKind.IdenticalCopies))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in group.Entries)
            {
                if (seen.Add(entry.Value)) continue;
                int start = parents[entry.IdLineIndex] >= 0 ? parents[entry.IdLineIndex] : entry.IdLineIndex;
                remove.Add((start, CountedList.BlockEnd(doc, start)));
            }
        }
        if (remove.Count == 0) return 0;

        foreach (var (start, end) in remove.OrderByDescending(r => r.Start)) doc.Lines.RemoveRange(start, end - start);
        if (doc.Lines.FirstOrDefault(l => l.Key == "NumStrings") is { } count)
            count.SetRawValue(SinsValue.FormatInt(table.TotalCount - remove.Count));

        string relative = Languages.StringFilePath(mod, table.Language);
        doc.ApplyConvention(ConventionCheck.ExpectedConvention(workspace.Stack, relative, doc.HasNonAsciiText));
        SafeWrite.Text(doc, Path.Combine(mod.RootPath, relative.Replace('/', Path.DirectorySeparatorChar)));
        mod.Refresh();
        return remove.Count;
    }
}
