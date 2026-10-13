using SinsEditor.Core.Documents;
using SinsEditor.Core.Index;

namespace SinsEditor.Core.Editing;

/// <summary>Replace <paramref name="RemoveCount"/> lines at <paramref name="Start"/> with <paramref name="Insert"/>.</summary>
public sealed record Splice(int Start, int RemoveCount, IReadOnlyList<SinsLine> Insert);

public enum EditKind
{
    /// <summary>Only values changed; line positions are the same.</summary>
    Values,
    /// <summary>Lines were inserted or removed.</summary>
    Structure,
    Saved,
}

/// <summary>
/// Unsaved changes to one entity: a working copy of its document with undo/redo.
/// Every change is a list of <see cref="Splice"/>s applied as one undo step.
/// </summary>
public sealed class EntityEditSession
{
    private sealed record AppliedSplice(int Start, IReadOnlyList<SinsLine> Removed, IReadOnlyList<SinsLine> Inserted);

    private sealed record Edit(string Description, IReadOnlyList<AppliedSplice> Splices, int StateBefore, int StateAfter, bool Structural);

    private readonly Stack<Edit> _undo = new();
    private readonly Stack<Edit> _redo = new();
    private int _state;
    private int _nextState = 1;
    private int _savedState;

    public EntityEditSession(EntityRecord original)
    {
        Original = original;
        Document = SinsDocument.Parse(original.Document.ToBytes());
    }

    /// <summary>The entity as last loaded or saved.</summary>
    public EntityRecord Original { get; private set; }
    public SinsDocument Document { get; }
    public string EntityName => Original.Name;

    public bool IsDirty => _state != _savedState;

    /// <summary>Format chosen for the next save; null means the workspace default for this entity.</summary>
    public SaveFormat? Format { get; set; }
    public bool CanUndo => _undo.Count > 0;
    /// <summary>Number of steps that can be undone; lets callers see whether an operation added one.</summary>
    public int UndoCount => _undo.Count;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoDescription => _undo.TryPeek(out var e) ? e.Description : null;
    public string? RedoDescription => _redo.TryPeek(out var e) ? e.Description : null;

    public event Action<EditKind>? Changed;

    public void SetValue(int lineIndex, string rawValue)
    {
        var line = Document.Lines[lineIndex];
        if (line.Value == rawValue) return;
        var copy = Clone(line);
        copy.SetRawValue(rawValue);
        Apply($"Change {line.Key}", [new Splice(lineIndex, 1, [copy])]);
    }

    /// <summary>Applies splices in order as one undoable edit.</summary>
    public void Apply(string description, IReadOnlyList<Splice> splices)
    {
        if (splices.Count == 0) return;
        var applied = splices.Select(Do).ToList();
        bool structural = applied.Any(a => a.Removed.Count != a.Inserted.Count);
        int before = _state;
        _state = _nextState++;
        _undo.Push(new Edit(description, applied, before, _state, structural));
        _redo.Clear();
        Changed?.Invoke(structural ? EditKind.Structure : EditKind.Values);
    }

    /// <summary>Replaces all lines with the given text, e.g. from the raw text editor.</summary>
    public void ReplaceText(string text)
    {
        var lines = SplitLines(text, Document.DefaultEnding, keepFinalNewline: Document.Lines.LastOrDefault()?.Ending.Length > 0);
        if (lines.Select(l => l.Text + l.Ending).SequenceEqual(Document.Lines.Select(l => l.Text + l.Ending))) return;
        Apply("Edit text", [new Splice(0, Document.Lines.Count, lines)]);
    }

    public void Undo()
    {
        if (!_undo.TryPop(out var edit)) return;
        for (int i = edit.Splices.Count - 1; i >= 0; i--)
        {
            var s = edit.Splices[i];
            Document.Lines.RemoveRange(s.Start, s.Inserted.Count);
            Document.Lines.InsertRange(s.Start, s.Removed.Select(Clone));
        }
        _state = edit.StateBefore;
        _redo.Push(edit);
        Changed?.Invoke(edit.Structural ? EditKind.Structure : EditKind.Values);
    }

    public void Redo()
    {
        if (!_redo.TryPop(out var edit)) return;
        foreach (var s in edit.Splices)
        {
            Document.Lines.RemoveRange(s.Start, s.Removed.Count);
            Document.Lines.InsertRange(s.Start, s.Inserted.Select(Clone));
        }
        _state = edit.StateAfter;
        _undo.Push(edit);
        Changed?.Invoke(edit.Structural ? EditKind.Structure : EditKind.Values);
    }

    /// <summary>Called after the document was written; <paramref name="saved"/> is the entity as it is now on disk.</summary>
    public void MarkSaved(EntityRecord saved)
    {
        Original = saved;
        _savedState = _state;
        Changed?.Invoke(EditKind.Saved);
    }

    public string Text => string.Concat(Document.Lines.Select(l => l.Text + l.Ending));

    private AppliedSplice Do(Splice splice)
    {
        var removed = Document.Lines.GetRange(splice.Start, splice.RemoveCount).Select(Clone).ToList();
        var inserted = splice.Insert.Select(Clone).ToList();
        Document.Lines.RemoveRange(splice.Start, splice.RemoveCount);
        Document.Lines.InsertRange(splice.Start, inserted.Select(Clone));
        return new AppliedSplice(splice.Start, removed, inserted);
    }

    public static SinsLine Clone(SinsLine line) => SinsLine.Parse(line.Text, line.Ending);

    /// <summary>Splits text into lines, normalising every ending to <paramref name="ending"/>.</summary>
    public static List<SinsLine> SplitLines(string text, string ending, bool keepFinalNewline)
    {
        var raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        bool endsWithNewline = raw.Count > 1 && raw[^1].Length == 0;
        if (endsWithNewline) raw.RemoveAt(raw.Count - 1);
        var lines = raw.Select(r => SinsLine.Parse(r, ending)).ToList();
        if (lines.Count > 0 && !endsWithNewline && !keepFinalNewline) lines[^1].Ending = "";
        return lines;
    }
}
