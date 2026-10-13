using SinsEditor.Core.Documents;
using SinsEditor.Core.Editing;

namespace SinsEditor.Core.Research;

/// <summary>Edits of research subjects, each one undo step on the subject's edit session.</summary>
public static class ResearchEdits
{
    /// <summary>
    /// Moves a subject to a field, block and cell. If its tier equals its old column, the tier moves along
    /// (826 of 850 subjects in the test mod keep tier and column equal).
    /// </summary>
    public static void Move(EntityEditSession session, string field, int block, int column, int row, bool keepTierWithColumn = true)
    {
        var doc = session.Document;
        var root = doc.BuildTree();
        var splices = new List<Splice>();

        void Set(SinsNode? node, string raw)
        {
            if (node is null || node.Line!.Value == raw) return;
            var line = EntityEditSession.Clone(node.Line);
            line.SetRawValue(raw);
            splices.Add(new Splice(node.LineIndex, 1, [line]));
        }

        var fieldNode = root.Child("ResearchField");
        var location = root.Child("researchWindowLocation");
        if (fieldNode is null || location?.Child("block") is null || location.Child("pos") is not { } pos)
            throw new InvalidOperationException($"{session.EntityName} has no ResearchField or researchWindowLocation to change.");

        int? oldColumn = SinsValue.TryGetVector(pos.Value, out var p) && p.Length == 2 ? (int)p[0] : null;
        Set(fieldNode, SinsValue.FormatString(field));
        Set(location.Child("block"), SinsValue.FormatInt(block));
        Set(pos, $"[ {column} , {row} ]");
        if (keepTierWithColumn && root.Child("Tier") is { } tier && SinsValue.TryGetInt(tier.Value, out int t) && t == oldColumn)
            Set(tier, SinsValue.FormatInt(column));

        session.Apply($"Move {session.EntityName}", splices);
    }

    /// <summary>Adds a prerequisite (no-op if it's already there), keeping <c>NumResearchPrerequisites</c> right.</summary>
    public static bool AddPrerequisite(EntityEditSession session, string subject, int level = 1)
    {
        var doc = session.Document;
        var root = doc.BuildTree();
        if (root.Child("Prerequisites")?.Child("NumResearchPrerequisites") is not { } counterNode
            || CountedList.Find(doc, counterNode.LineIndex) is not { } list)
            throw new InvalidOperationException($"{session.EntityName} has no Prerequisites block.");
        if (FindPrerequisite(doc, list, subject) >= 0) return false;

        var counter = doc.Lines[list.CounterLine];
        List<SinsLine> lines;
        if (list.Items.Count > 0)
        {
            // Copy the last item's lines, so any extra keys and the file's indentation stay as they are.
            var last = list.Items[^1];
            lines = doc.Lines.GetRange(last.Start, last.End - last.Start).Select(EntityEditSession.Clone).ToList();
            foreach (var line in lines)
            {
                if (line.Key == "Subject") line.SetRawValue(SinsValue.FormatString(subject));
                else if (line.Key == "Level") line.SetRawValue(SinsValue.FormatInt(level));
            }
        }
        else
        {
            string ending = doc.DefaultEnding;
            lines =
            [
                SinsLine.Create(counter.Indent, "ResearchPrerequisite", "", ending),
                SinsLine.Create(counter.Indent + "\t", "Subject", SinsValue.FormatString(subject), ending),
                SinsLine.Create(counter.Indent + "\t", "Level", SinsValue.FormatInt(level), ending),
            ];
        }

        var newCounter = EntityEditSession.Clone(counter);
        newCounter.SetRawValue(SinsValue.FormatInt(list.Items.Count + 1));
        int insertAt = list.Items.Count > 0 ? list.Items[^1].End : list.CounterLine + 1;
        session.Apply($"Add prerequisite {subject}", [new Splice(insertAt, 0, lines), new Splice(list.CounterLine, 1, [newCounter])]);
        return true;
    }

    public static bool RemovePrerequisite(EntityEditSession session, string subject)
    {
        var doc = session.Document;
        var root = doc.BuildTree();
        if (root.Child("Prerequisites")?.Child("NumResearchPrerequisites") is not { } counterNode
            || CountedList.Find(doc, counterNode.LineIndex) is not { } list) return false;
        int index = FindPrerequisite(doc, list, subject);
        if (index < 0) return false;
        ListEdits.RemoveItem(session, list, index);
        return true;
    }

    private static int FindPrerequisite(SinsDocument doc, CountedList list, string subject)
    {
        for (int i = 0; i < list.Items.Count; i++)
        {
            var item = list.Items[i];
            for (int l = item.Start; l < item.End; l++)
            {
                if (doc.Lines[l].Key == "Subject" && SinsValue.TryGetString(doc.Lines[l].Value, out var s)
                    && string.Equals(s, subject, StringComparison.OrdinalIgnoreCase)) return i;
            }
        }
        return -1;
    }
}
