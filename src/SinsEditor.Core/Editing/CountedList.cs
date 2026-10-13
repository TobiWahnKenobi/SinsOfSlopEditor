using SinsEditor.Core.Documents;

namespace SinsEditor.Core.Editing;

/// <summary>An item of a counted list: its first line and the line after its last child.</summary>
public readonly record struct ListItem(int Start, int End);

/// <summary>
/// A list preceded by a counter line, e.g. <c>count 9</c> + 9 × <c>entityDefName</c>, <c>NumWeapons 1</c> + <c>Weapon</c> block,
/// <c>NumSoundsFor:ONSELECTED 4</c> + 4 × <c>SoundID</c>. The game reads exactly as many items as the counter says,
/// so adding or removing items must update it.
/// </summary>
public sealed record CountedList(int CounterLine, string? ItemKey, IReadOnlyList<ListItem> Items)
{
    /// <summary>Finds the list a line belongs to, as its counter or as one of its items (or a line inside an item).</summary>
    public static CountedList? Find(SinsDocument doc, int lineIndex)
    {
        if (lineIndex <= 0 || lineIndex >= doc.Lines.Count) return null;
        var parents = doc.GetParentIndices();
        int line = lineIndex;
        while (line >= 0)
        {
            if (AsCounter(doc, parents, line) is { } asCounter) return asCounter;
            if (AsItem(doc, parents, line) is { } asItem) return asItem;
            line = parents[line];
        }
        return null;
    }

    public int DeclaredCount(SinsDocument doc) =>
        SinsValue.TryGetInt(doc.Lines[CounterLine].Value, out int n) ? n : -1;

    public bool IsConsistent(SinsDocument doc) => DeclaredCount(doc) == Items.Count;

    public int IndexOfItemContaining(int lineIndex) => Items.ToList().FindIndex(i => lineIndex >= i.Start && lineIndex < i.End);

    /// <summary>
    /// A line is a counter when it holds an integer and the run of same-key siblings right after it has that length,
    /// or when its key reads like a counter (then a mismatch is a broken list, and 0 with no matching run an empty one).
    /// Keys that don't read like counters only qualify with a matching run of two or more.
    /// </summary>
    private static CountedList? AsCounter(SinsDocument doc, int[] parents, int counter)
    {
        var lines = doc.Lines;
        if (!SinsValue.TryGetInt(lines[counter].Value, out int declared) || declared < 0) return null;
        bool countLike = IsCountLike(lines[counter].Key);
        var siblings = SiblingsAfter(doc, parents, counter).ToList();
        if (siblings.Count == 0 || lines[siblings[0]].Key == lines[counter].Key)
            return declared == 0 && countLike ? new CountedList(counter, null, []) : null;

        string itemKey = lines[siblings[0]].Key;
        var run = siblings.TakeWhile(s => lines[s].Key == itemKey).Select(s => new ListItem(s, BlockEnd(doc, s))).ToList();
        // Keys that don't read like counters need a run of at least two to rule out coincidences like
        // "MaxNumResearchLevels 1" followed by a single "priority" line.
        if (run.Count == declared && (countLike || declared >= 2)) return new CountedList(counter, itemKey, run);
        if (!countLike) return null;
        return declared == 0 ? new CountedList(counter, null, []) : new CountedList(counter, itemKey, run);
    }
    private static CountedList? AsItem(SinsDocument doc, int[] parents, int item)
    {
        var lines = doc.Lines;
        string key = lines[item].Key;
        if (key.Length == 0) return null;
        // Walk back over earlier items with the same key to the line before the run.
        int previous = PreviousSibling(doc, parents, item);
        while (previous >= 0 && lines[previous].Key == key) previous = PreviousSibling(doc, parents, previous);
        if (previous < 0) return null;
        var list = AsCounter(doc, parents, previous);
        return list?.ItemKey == key ? list : null;
    }

    private static IEnumerable<int> SiblingsAfter(SinsDocument doc, int[] parents, int line)
    {
        int parent = parents[line];
        int depth = doc.Lines[line].Depth;
        for (int i = line + 1; i < doc.Lines.Count; i++)
        {
            if (doc.Lines[i].IsBlank) continue;
            if (doc.Lines[i].Depth < depth || (doc.Lines[i].Depth == depth && parents[i] != parent)) yield break;
            if (parents[i] == parent) yield return i;
        }
    }

    private static int PreviousSibling(SinsDocument doc, int[] parents, int line)
    {
        int parent = parents[line];
        for (int i = line - 1; i > parent && i > 0; i--)
        {
            if (doc.Lines[i].IsBlank) continue;
            if (parents[i] == parent) return i;
        }
        return -1;
    }

    /// <summary>The line after <paramref name="start"/> and all its children.</summary>
    public static int BlockEnd(SinsDocument doc, int start)
    {
        int depth = doc.Lines[start].Depth;
        int i = start + 1;
        while (i < doc.Lines.Count && (doc.Lines[i].IsBlank || doc.Lines[i].Depth > depth)) i++;
        return i;
    }

    public static bool IsCountLike(string key) =>
        key.Equals("count", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("num", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("Count", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Edits on counted lists that keep the counter in sync, each as one undo step.</summary>
public static class ListEdits
{
    public static void DuplicateItem(EntityEditSession session, CountedList list, int itemIndex)
    {
        var doc = session.Document;
        var item = list.Items[itemIndex];
        var copy = doc.Lines.GetRange(item.Start, item.End - item.Start).Select(EntityEditSession.Clone).ToList();
        session.Apply($"Duplicate {doc.Lines[item.Start].Key}",
        [
            new Splice(item.End, 0, copy),
            CounterSplice(doc, list, list.Items.Count + 1),
        ]);
    }

    public static void RemoveItem(EntityEditSession session, CountedList list, int itemIndex)
    {
        var doc = session.Document;
        var item = list.Items[itemIndex];
        session.Apply($"Remove {doc.Lines[item.Start].Key}",
        [
            new Splice(item.Start, item.End - item.Start, []),
            CounterSplice(doc, list, list.Items.Count - 1),
        ]);
    }

    /// <summary>Adds an item after the last one by copying it; for an empty list of known key, adds <c>key ""</c>.</summary>
    public static bool AddItem(EntityEditSession session, CountedList list, string? itemKey = null)
    {
        if (list.Items.Count > 0)
        {
            DuplicateItem(session, list, list.Items.Count - 1);
            return true;
        }
        string? key = list.ItemKey ?? itemKey;
        if (key is null) return false;
        var doc = session.Document;
        var counter = doc.Lines[list.CounterLine];
        var line = SinsLine.Create(counter.Indent, key, "\"\"", doc.DefaultEnding);
        session.Apply($"Add {key}",
        [
            new Splice(list.CounterLine + 1, 0, [line]),
            CounterSplice(doc, list, 1),
        ]);
        return true;
    }

    private static Splice CounterSplice(SinsDocument doc, CountedList list, int count)
    {
        var counter = EntityEditSession.Clone(doc.Lines[list.CounterLine]);
        counter.SetRawValue(SinsValue.FormatInt(count));
        return new Splice(list.CounterLine, 1, [counter]);
    }
}
