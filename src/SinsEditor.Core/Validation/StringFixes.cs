using SinsEditor.Core.Documents;
using SinsEditor.Core.Editing;
using SinsEditor.Core.FileSystem;
using SinsEditor.Core.Index;

namespace SinsEditor.Core.Validation;

public static class StringFixes
{
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
