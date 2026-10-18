using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using SinsEditor.App.Navigation;
using SinsEditor.Core;
using SinsEditor.Core.Documents;
using SinsEditor.Core.Editing;
using SinsEditor.Core.Index;

namespace SinsEditor.App.Editing;

/// <summary>
/// The form editor for one entity: a row per line, typed by what the line holds and what the data says about it.
/// Rows are rebuilt when lines are inserted or removed, and reloaded when only values changed (e.g. undo).
/// </summary>
public sealed partial class PropertyEditorViewModel : ObservableObject
{
    private readonly ModWorkspace _workspace;
    private readonly CandidateProvider _candidates;
    private readonly Action<NavTarget> _navigate;

    [ObservableProperty]
    private string _filterText = "";

    public PropertyEditorViewModel(ModWorkspace workspace, EntityEditSession session, CandidateProvider candidates, Action<NavTarget> navigate)
    {
        _workspace = workspace;
        _candidates = candidates;
        _navigate = navigate;
        Session = session;
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is PropertyRowViewModel r && Matches(r);
        Rebuild();
        session.Changed += OnSessionChanged;
    }

    private IReadOnlyList<Core.Validation.Problem> _problems = [];

    public EntityEditSession Session { get; }

    /// <summary>Marks the rows of lines with problems (0-based line indices as in the file).</summary>
    public void SetProblems(IReadOnlyList<Core.Validation.Problem> problems)
    {
        _problems = problems;
        ApplyProblems();
    }

    private void ApplyProblems()
    {
        var byLine = _problems.Where(p => p.LineIndex != null).ToLookup(p => p.LineIndex!.Value);
        foreach (var row in Rows)
        {
            var lineProblems = byLine[row.LineIndex].ToList();
            row.ProblemText = lineProblems.Count == 0 ? null : string.Join("\n", lineProblems.Select(p => p.Message));
            row.ProblemIsError = lineProblems.Any(p => p.Severity == Core.Validation.ProblemSeverity.Error);
        }
    }

    /// <summary>The row showing a line, or the closest row above it (blank lines have no row).</summary>
    public PropertyRowViewModel? RowForLine(int lineIndex) =>
        Rows.LastOrDefault(r => r.LineIndex <= lineIndex) ?? Rows.FirstOrDefault();
    public ObservableCollection<PropertyRowViewModel> Rows { get; } = [];
    public ICollectionView View { get; }

    /// <summary>Stops listening to the session; call when the page goes away.</summary>
    public void Detach() => Session.Changed -= OnSessionChanged;

    partial void OnFilterTextChanged(string value) => View.Refresh();

    private void OnSessionChanged(EditKind kind)
    {
        if (kind == EditKind.Structure) Rebuild();
        else foreach (var row in Rows) row.Reload();
    }

    private void Rebuild()
    {
        var doc = Session.Document;
        var lines = doc.Lines;
        var parents = doc.GetParentIndices();

        // Lists are found from their counters; every item's first line gets a reference to its list.
        var counters = new Dictionary<int, CountedList>();
        var items = new Dictionary<int, (CountedList List, int Index)>();
        for (int i = 1; i < lines.Count; i++)
        {
            if (lines[i].ValueKind != SinsValueKind.Integer || CountedList.Find(doc, i) is not { } list || list.CounterLine != i) continue;
            counters[i] = list;
            for (int n = 0; n < list.Items.Count; n++) items[list.Items[n].Start] = (list, n);
        }

        Rows.Clear();
        for (int i = 1; i < lines.Count; i++)
        {
            if (lines[i].IsBlank) continue;
            string context = PropertyHints.ContextOf(lines, parents, i);
            var hint = _workspace.Hints.Get(context);
            var kind = KindOf(lines[i], hint);
            if (counters.TryGetValue(i, out var counter))
                Rows.Add(new PropertyRowViewModel(this, i, context, kind, hint, counter, -1, isCounter: true));
            else if (items.TryGetValue(i, out var item))
                Rows.Add(new PropertyRowViewModel(this, i, context, kind, hint, item.List, item.Index, isCounter: false));
            else
                Rows.Add(new PropertyRowViewModel(this, i, context, kind, hint, null, -1, isCounter: false));
        }
        ApplyProblems();
    }

    private static RowKind KindOf(SinsLine line, PropertyHint? hint)
    {
        if (!line.HasValue) return RowKind.Section;
        return line.ValueKind switch
        {
            SinsValueKind.String when hint?.Reference != null => RowKind.Reference,
            SinsValueKind.String when hint?.KnownValues.Count > 0 => RowKind.Choice,
            SinsValueKind.String => RowKind.Text,
            SinsValueKind.Bool => RowKind.Bool,
            SinsValueKind.Integer or SinsValueKind.Float => RowKind.Number,
            _ => RowKind.Raw,
        };
    }

    private bool Matches(PropertyRowViewModel row) =>
        FilterText.Length == 0
        || row.Key.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
        || row.Value.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
        || (row.ValueDescription?.Contains(FilterText, StringComparison.CurrentCultureIgnoreCase) ?? false)
        || (row.HasString && row.StringText.Contains(FilterText, StringComparison.CurrentCultureIgnoreCase));

    // ---- Services for rows ----

    public bool Exists(ReferenceKind kind, string name) => _workspace.Index.Exists(kind, name);

    public IReadOnlyList<PickerItem> Candidates(PropertyRowViewModel row) =>
        row.Hint?.Reference is { } kind ? _candidates.For(kind, row.Hint.TargetEntityTypes) : [];

    public string? Describe(ReferenceKind kind, string value) => kind switch
    {
        ReferenceKind.Entity when _workspace.Index.Entities.TryGetValue(value, out var e) => _workspace.Index.DisplayName(e),
        ReferenceKind.String => _workspace.Index.Strings.Get(value) is { } s ? $"“{(s.Length > 80 ? s[..80] + "…" : s)}”" : null,
        _ => null,
    };

    public void Navigate(NavTarget target) => _navigate(target);

    public string StringText(string id) => _workspace.Index.Strings.Get(id) ?? "";

    /// <summary>Writes a string's text to the mod's string file; null if that worked, otherwise why not.</summary>
    public string? SetString(string id, string text)
    {
        try
        {
            StringEdits.SetString(_workspace, id, text);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
        // Other rows may show the same string, and a new string resolves their errors.
        foreach (var row in Rows) row.Reload();
        StringsChanged?.Invoke();
        return null;
    }

    /// <summary>Raised after a string was written to the string file.</summary>
    public event Action? StringsChanged;

    /// <summary>
    /// Item key for an empty list: taken from another list in the same entity whose counter has the same key
    /// (e.g. an empty <c>Page:1</c> next to a filled <c>Page:0</c>).
    /// </summary>
    public string? GuessItemKey(PropertyRowViewModel counterRow)
    {
        if (counterRow.List?.ItemKey is { } known) return known;
        return Rows
            .Where(r => r.IsCounter && r.Key == counterRow.Key && r.List?.ItemKey != null)
            .Select(r => r.List!.ItemKey)
            .FirstOrDefault();
    }
}
