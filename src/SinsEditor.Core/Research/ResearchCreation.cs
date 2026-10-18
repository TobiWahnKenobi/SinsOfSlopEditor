using SinsEditor.Core.Documents;
using SinsEditor.Core.Editing;
using SinsEditor.Core.Index;

namespace SinsEditor.Core.Research;

/// <summary>New research subjects made right on the tree: a copy of a template placed in a free cell.</summary>
public static class ResearchCreation
{
    /// <summary>
    /// Plans a new subject <paramref name="newName"/> copied from <paramref name="template"/> (its strings copied under new
    /// IDs, so they can be changed without touching the template's), placed at the cell with the tier of its column and no
    /// prerequisites, listed in the manifest and added to the race's research list. Works on the saved files, like the
    /// other entity operations; the caller reloads afterwards.
    /// </summary>
    public static ChangeSet PlanNewSubject(ModWorkspace workspace, EntityRecord race, EntityRecord template, string newName,
        string field, int block, int column, int row)
    {
        var plan = EntityOperations.PlanDuplicate(workspace, template, newName, addToRaces: false, copyStrings: true);
        string path = $"GameInfo/{newName}.entity";
        var change = plan.Changes.First(c => string.Equals(c.RelativePath, path, StringComparison.OrdinalIgnoreCase));

        var subject = new EntityEditSession(template with { Name = newName, Document = change.Content! });
        ResearchEdits.Move(subject, field, block, column, row, keepTierWithColumn: false);
        if (subject.Document.BuildTree().Child("Tier") is { } tier)
            subject.SetValue(tier.LineIndex, SinsValue.FormatInt(column));
        foreach (string prerequisite in Prerequisites(subject.Document)) ResearchEdits.RemovePrerequisite(subject, prerequisite);
        plan.Set(change with { Content = subject.Document, Description = $"New research subject, a copy of {template.Name}, at {ResearchTree.FieldTitle(field)} block {block} column {column} row {row}" });

        var raceSession = new EntityEditSession(race);
        ResearchEdits.AddToResearchList(raceSession, newName);
        plan.Set(new FileChange(race.File.RelativePath, raceSession.Document, $"Add {newName} to the research list"));
        return plan;
    }

    /// <summary>A template for a new subject in a cell: the nearest subject of the race in the same block, else the field, else any.</summary>
    public static ResearchSubject? SuggestTemplate(ResearchTree tree, string field, int block, int column, int row) =>
        tree.Subjects
            .Where(s => s.HasPosition)
            .OrderBy(s => s.Field == field ? 0 : 1)
            .ThenBy(s => s.Field == field && s.Block == block ? 0 : 1)
            .ThenBy(s => Math.Abs((s.Column ?? 0) - column) + Math.Abs((s.Row ?? 0) - row))
            .FirstOrDefault();

    /// <summary>A free entity name like <c>&lt;template&gt;_New</c>, <c>_New2</c>, …</summary>
    public static string SuggestName(ModWorkspace workspace, string template)
    {
        string baseName = template + "_New", name = baseName;
        for (int n = 2; EntityOperations.ValidateNewName(workspace, name) != null; n++) name = baseName + n;
        return name;
    }

    private static List<string> Prerequisites(SinsDocument doc) =>
        doc.BuildTree().Child("Prerequisites")?.Descendants()
            .Where(n => n.Key == "Subject" && SinsValue.TryGetString(n.Value, out _))
            .Select(n => { SinsValue.TryGetString(n.Value, out var s); return s!; })
            .ToList() ?? [];
}
