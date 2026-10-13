using SinsEditor.Core.Documents;
using SinsEditor.Core.Validation;

namespace SinsEditor.Core.Editing;

/// <summary>A planned change to one file of the edited mod: new content, or deletion when <paramref name="Content"/> is null.</summary>
public sealed record FileChange(string RelativePath, SinsDocument? Content, string Description)
{
    public bool IsDelete => Content is null;
}

/// <summary>
/// Changes to several files that belong together (e.g. a rename touches the entity, the manifest and every user).
/// Planned first so they can be shown before anything is written.
/// </summary>
public sealed class ChangeSet(string title)
{
    private readonly List<FileChange> _changes = [];

    public string Title { get; } = title;
    public IReadOnlyList<FileChange> Changes => _changes;
    /// <summary>Things the user should know that aren't file changes, e.g. references that can't be updated.</summary>
    public List<string> Notes { get; } = [];

    /// <summary>Adds or replaces the change for a path.</summary>
    public void Set(FileChange change)
    {
        _changes.RemoveAll(c => string.Equals(c.RelativePath, change.RelativePath, StringComparison.OrdinalIgnoreCase));
        _changes.Add(change);
    }

    /// <summary>
    /// Writes everything into the edited mod. Text follows the game's conventions; files are written via a temporary file.
    /// Entity files that are binary in the mod are written as text (the format can be changed when saving later).
    /// </summary>
    public void Apply(ModWorkspace workspace)
    {
        var mod = workspace.Stack.EditedMod ?? throw new InvalidOperationException("No mod is open for editing.");
        foreach (var change in _changes)
        {
            string target = Path.Combine(mod.RootPath, change.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (change.Content is null)
            {
                if (File.Exists(target)) File.Delete(target);
                continue;
            }
            var doc = SinsDocument.Parse(change.Content.ToBytes());
            doc.ApplyConvention(ConventionCheck.ExpectedConvention(workspace.Stack, change.RelativePath, doc.HasNonAsciiText));
            SafeWrite.Text(doc, target);
        }
        mod.Refresh();
    }
}
