using SinsEditor.Core.Documents;
using SinsEditor.Core.FileSystem;
using SinsEditor.Core.Index;
using SinsEditor.Core.Validation;

namespace SinsEditor.Core.Editing;

/// <summary>Changes the text of single strings in the edited mod's string file, from wherever they're used.</summary>
public static class StringEdits
{
    /// <summary>
    /// Sets the text of <paramref name="id"/> in the edited mod's string file for the workspace's language: the first
    /// definition is changed if the ID exists, otherwise a <c>StringInfo</c> block is added and <c>NumStrings</c> goes up.
    /// A table that comes from a lower layer is copied into the mod first. The index's table is replaced by the new one.
    /// Returns the previous text, or null if the string is new.
    /// </summary>
    public static string? SetString(ModWorkspace workspace, string id, string value)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Enter a string ID.", nameof(id));
        if (id.Contains('"') || value.Contains('"')) throw new ArgumentException("Strings can't contain quotes.");
        if (value.Contains('\r') || value.Contains('\n')) throw new ArgumentException("Strings are one line; use \\n for line breaks.");
        var table = workspace.Index.Strings;
        var mod = workspace.Stack.EditedMod ?? throw new InvalidOperationException("No mod is open for editing.");
        var doc = table.Document is { } d ? SinsDocument.Parse(d.ToBytes()) : SinsDocument.Parse("TXT\r\nNumStrings 0\r\n"u8.ToArray());

        string? previous = null;
        var existing = table.AllOf(id);
        if (existing.Count > 0)
        {
            previous = existing[0].Value;
            // The Value line follows its ID line within the block.
            int valueLine = -1;
            for (int i = existing[0].IdLineIndex + 1; i < doc.Lines.Count; i++)
            {
                if (doc.Lines[i].Key == "Value") { valueLine = i; break; }
                if (doc.Lines[i].Key is "StringInfo" or "ID") break;
            }
            if (valueLine < 0) throw new InvalidOperationException($"String '{id}' has no Value line.");
            doc.Lines[valueLine].SetRawValue(SinsValue.FormatString(value));
        }
        else
        {
            int end = doc.Lines.Count;
            doc.InsertLine(end, "", "StringInfo");
            doc.InsertLine(end + 1, "\t", "ID", SinsValue.FormatString(id));
            doc.InsertLine(end + 2, "\t", "Value", SinsValue.FormatString(value));
            if (doc.Lines.FirstOrDefault(l => l.Key == "NumStrings") is { } count && SinsValue.TryGetInt(count.Value, out int n))
                count.SetRawValue(SinsValue.FormatInt(n + 1));
        }

        string relative = Languages.StringFilePath(mod, table.Language);
        doc.ApplyConvention(ConventionCheck.ExpectedConvention(workspace.Stack, relative, doc.HasNonAsciiText));
        string path = Path.Combine(mod.RootPath, relative.Replace('/', Path.DirectorySeparatorChar));
        SafeWrite.Text(doc, path);
        mod.Refresh();
        if (Languages.ResolveStringFile(workspace.Stack, table.Language) is { } file)
            workspace.ReplaceStrings(StringTable.Load(table.Language, file, SinsDocument.Load(file.AbsolutePath)));
        return previous;
    }
}
