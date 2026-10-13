using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SinsEditor.App.Imaging;
using SinsEditor.App.Navigation;
using SinsEditor.App.ViewModels;
using SinsEditor.Core;
using SinsEditor.Core.Editing;
using SinsEditor.Core.Index;
using SinsEditor.Core.Research;
using SinsEditor.Core.Validation;

namespace SinsEditor.App.Research;

public sealed record FieldTab(string Field, string Title, BitmapSource? Icon, int Count, bool HasIssues);

/// <summary>A block drawn on the canvas, in the research window's pixel coordinates.</summary>
public sealed record BlockVisual(ResearchBlock Block, Rect Bounds, BitmapSource? Backdrop, string Title, BitmapSource? Picture, Point PicturePosition);

/// <summary>An empty grid cell shown by the editor overlay (drop target).</summary>
public sealed record CellVisual(Rect Bounds);

public sealed partial class SubjectVisual(ResearchSubject subject, Rect bounds, string displayName, BitmapSource? icon, BitmapSource? backdrop,
    string? issue, bool issueIsError) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public ResearchSubject Subject { get; } = subject;
    public Rect Bounds { get; } = bounds;
    public string DisplayName { get; } = displayName;
    public BitmapSource? Icon { get; } = icon;
    public BitmapSource? Backdrop { get; } = backdrop;
    public string? Issue { get; } = issue;
    public bool HasIssue => Issue != null;
    public bool IssueIsError { get; } = issueIsError;
    public string ToolTip => Issue is null ? $"{DisplayName}\n{Subject.Name}" : $"{DisplayName}\n{Subject.Name}\n\n{Issue}";
}

public sealed partial class ArrowVisual(ResearchLink link, PointCollection line, PointCollection head, string tooltip) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public ResearchLink Link { get; } = link;
    public PointCollection Line { get; } = line;
    public PointCollection Head { get; } = head;
    public bool IsUnsupported => Link.Shape == ArrowShape.Unsupported;
    public string ToolTip { get; } = tooltip;
}

public sealed record PrerequisiteRow(ResearchPrerequisite Prerequisite, string Display, bool Exists);

public sealed record SubjectChoice(string Name, string Display)
{
    public override string ToString() => Display;
}

public sealed record IssueRow(ProblemSeverity Severity, string Message, string? Subject);

/// <summary>
/// Graphical research tree editor for one race, drawn from the game's own layout and images.
/// Every subject is edited through the workspace's edit sessions, so changes show up everywhere and are saved with "Save all".
/// </summary>
public sealed partial class ResearchViewModel : PageViewModel
{
    public const double CanvasWidth = 814;
    public const double CanvasHeight = 749;

    private readonly ModWorkspace _workspace;
    private readonly Action<NavTarget> _navigate;
    private readonly Dictionary<string, BitmapSource?> _imageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EntityEditSession> _watched = [];
    /// <summary>Sessions in the order this page edited them, for page-level undo/redo.</summary>
    private readonly Stack<EntityEditSession> _undo = new();
    private readonly Stack<EntityEditSession> _redo = new();
    private bool _applying;

    [ObservableProperty]
    private ResearchTree _tree;

    [ObservableProperty]
    private string _selectedField = "Combat";

    [ObservableProperty]
    private SubjectVisual? _selectedSubject;

    [ObservableProperty]
    private ArrowVisual? _selectedArrow;

    [ObservableProperty]
    private bool _showGrid = true;

    [ObservableProperty]
    private bool _showNames;

    [ObservableProperty]
    private SubjectChoice? _prerequisiteToAdd;

    [ObservableProperty]
    private string? _statusMessage;

    public ResearchViewModel(ModWorkspace workspace, EntityRecord race, Action<NavTarget> navigate)
    {
        _workspace = workspace;
        _navigate = navigate;
        Race = race;
        RaceName = workspace.Index.DisplayName(race) ?? race.Name;
        _tree = Build();
        SelectedField = ResearchTree.Fields.FirstOrDefault(f => Tree.Subjects.Any(s => s.Field == f)) ?? "Combat";
        Watch();
        Refresh();
    }

    public EntityRecord Race { get; }
    public string RaceName { get; }
    public override string Title => $"Research · {RaceName}";
    public override string? Breadcrumb => "Races";
    public override bool IsDirty => _watched.Any(s => s.IsDirty);

    public ObservableCollection<FieldTab> FieldTabs { get; } = [];
    public ObservableCollection<BlockVisual> Blocks { get; } = [];
    public ObservableCollection<CellVisual> Cells { get; } = [];
    public ObservableCollection<SubjectVisual> Subjects { get; } = [];
    public ObservableCollection<ArrowVisual> Arrows { get; } = [];
    public ObservableCollection<SubjectChoice> Unplaced { get; } = [];
    public ObservableCollection<IssueRow> Issues { get; } = [];
    public ObservableCollection<PrerequisiteRow> SelectedPrerequisites { get; } = [];
    public ObservableCollection<string> SelectedUnlocks { get; } = [];
    public IReadOnlyList<SubjectChoice> AllSubjects { get; private set; } = [];
    public BitmapSource? ContentBackdrop { get; private set; }

    public bool HasSelection => SelectedSubject != null;
    public string SelectedTitle => SelectedSubject?.DisplayName ?? "";
    public string SelectedDetails => SelectedSubject?.Subject is { } s
        ? $"{s.Name}\n{ResearchTree.FieldTitle(s.Field ?? "?")} · block {s.Block} · column {s.Column} · row {s.Row} · tier {s.Tier} · {s.MaxLevels} level{(s.MaxLevels == 1 ? "" : "s")}"
        : "";
    public string IssueSummary => Issues.Count switch { 0 => "No problems", 1 => "1 problem", var n => $"{n} problems" };
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    // ---- Building ----

    private ResearchTree Build() => ResearchTree.Build(_workspace, Race, e => _workspace.SessionFor(e).Document);

    /// <summary>Listens to the race's and every subject's session, so edits anywhere (also undo in other tabs) redraw.</summary>
    private void Watch()
    {
        foreach (var s in _watched) s.Changed -= OnSessionChanged;
        _watched.Clear();
        foreach (var entity in Tree.Subjects.Select(s => s.Entity).Append(Race))
        {
            var session = _workspace.SessionFor(entity);
            session.Changed += OnSessionChanged;
            _watched.Add(session);
        }
    }

    public override void Detach()
    {
        foreach (var s in _watched) s.Changed -= OnSessionChanged;
    }

    private void OnSessionChanged(EditKind kind)
    {
        if (_applying) return;
        Tree = Build();
        Refresh();
        OnPropertyChanged(nameof(IsDirty));
    }

    partial void OnSelectedFieldChanged(string value) => Refresh();
    partial void OnShowGridChanged(bool value) => Refresh();

    partial void OnSelectedSubjectChanged(SubjectVisual? oldValue, SubjectVisual? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null)
        {
            newValue.IsSelected = true;
            SelectedArrow = null;
        }
        RefreshSelection();
    }

    partial void OnSelectedArrowChanged(ArrowVisual? oldValue, ArrowVisual? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null) newValue.IsSelected = true;
    }

    /// <summary>Rebuilds the visuals of the selected field from the current tree, keeping the selection.</summary>
    private void Refresh()
    {
        string? selectedName = SelectedSubject?.Subject.Name;
        var index = _workspace.Index;
        var tree = Tree;

        ContentBackdrop = tree.ContentBackdrop is { } cb ? Image(cb, () => ImageConverter.RenderBrush(_workspace.Textures, cb)) : null;
        OnPropertyChanged(nameof(ContentBackdrop));

        FieldTabs.Clear();
        foreach (string field in ResearchTree.Fields.Where(f => tree.BlocksOf(f).Any() || tree.Subjects.Any(s => s.Field == f)))
        {
            string? iconBrush = Facets.TopLevelValues(Race with { Document = _workspace.SessionFor(Race).Document }, "ResearchFieldButtonIcon" + field).FirstOrDefault();
            var subjectsInField = tree.Subjects.Where(s => s.Field == field).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            FieldTabs.Add(new FieldTab(field, ResearchTree.FieldTitle(field),
                iconBrush is null ? null : Image(iconBrush, () => ImageConverter.RenderBrush(_workspace.Textures, iconBrush)),
                subjectsInField.Count, tree.Issues.Any(i => i.Subject != null && subjectsInField.Contains(i.Subject))));
        }

        Blocks.Clear();
        Cells.Clear();
        tree.Layouts.TryGetValue(SelectedField, out var layout);
        foreach (var block in tree.BlocksOf(SelectedField))
        {
            var a = block.Area;
            var picture = block.Picture is { } p ? Image(p, () => ImageConverter.RenderBrush(_workspace.Textures, p)) : null;
            var backdrop = block.Backdrop is { } b ? Image($"{b}@{a.Width}x{a.Height}", () => Sized(b, a.Width, a.Height)) : null;
            string title = block.TitleStringId is { } t ? index.Strings.Get(t) ?? t : "";
            Blocks.Add(new BlockVisual(block, new Rect(a.X, a.Y, a.Width, a.Height), backdrop, title, picture,
                new Point(a.X + tree.PictureTopLeft.X, a.Y + tree.PictureTopLeft.Y)));
            if (layout is null || !ShowGrid) continue;
            var (cols, rows) = block.GridSize(layout);
            for (int c = 0; c < cols; c++)
            for (int r = 0; r < rows; r++)
            {
                var o = block.CellOrigin(layout, c, r);
                Cells.Add(new CellVisual(new Rect(o.X, o.Y, layout.ButtonSize.X, layout.ButtonSize.Y)));
            }
        }

        var issuesBySubject = tree.Issues.Where(i => i.Subject != null).ToLookup(i => i.Subject!, StringComparer.OrdinalIgnoreCase);
        Subjects.Clear();
        foreach (var subject in tree.Subjects.Where(s => s.Field == SelectedField))
        {
            if (tree.CellOf(subject) is not { } cell) continue;
            var size = cell.Layout.ButtonSize;
            var issues = issuesBySubject[subject.Name].Where(i => i.Severity != ProblemSeverity.Info).ToList();
            var visual = new SubjectVisual(subject, new Rect(cell.Origin.X, cell.Origin.Y, size.X, size.Y),
                index.DisplayName(subject.Entity) ?? subject.Name,
                subject.HudIcon is { } icon ? Image($"{icon}@{size.X - 4}x{size.Y - 4}", () => Sized(icon, size.X - 4, size.Y - 4)) : null,
                cell.Layout.ButtonBackdrop is { } bb ? Image($"{bb}@{size.X}x{size.Y}", () => Sized(bb, size.X, size.Y)) : null,
                issues.Count == 0 ? null : string.Join("\n", issues.Select(i => i.Message)), issues.Any(i => i.Severity == ProblemSeverity.Error));
            Subjects.Add(visual);
        }

        Arrows.Clear();
        foreach (var link in tree.Links.Where(l => l.To.Field == SelectedField))
        {
            if (ArrowFor(link) is { } arrow) Arrows.Add(arrow);
        }

        Unplaced.Clear();
        foreach (var s in tree.Unplaced) Unplaced.Add(new SubjectChoice(s.Name, $"{index.DisplayName(s.Entity) ?? s.Name}  ({s.Field ?? "no field"} {s.Block}, {s.Column}/{s.Row})"));
        AllSubjects = tree.Subjects
            .Select(s => new SubjectChoice(s.Name, $"{index.DisplayName(s.Entity) ?? s.Name}  ({ResearchTree.FieldTitle(s.Field ?? "?")})"))
            .OrderBy(c => c.Display, StringComparer.CurrentCultureIgnoreCase).ToList();
        OnPropertyChanged(nameof(AllSubjects));

        Issues.Clear();
        foreach (var issue in tree.Issues.OrderByDescending(i => i.Severity)) Issues.Add(new IssueRow(issue.Severity, issue.Message, issue.Subject));
        OnPropertyChanged(nameof(IssueSummary));

        SelectedSubject = Subjects.FirstOrDefault(s => s.Subject.Name == selectedName);
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedDetails));
        SelectedPrerequisites.Clear();
        SelectedUnlocks.Clear();
        if (SelectedSubject?.Subject is not { } s) return;
        var index = _workspace.Index;
        foreach (var pre in s.Prerequisites)
        {
            var target = Tree.Subject(pre.Subject);
            string display = target != null ? index.DisplayName(target.Entity) ?? pre.Subject : pre.Subject;
            SelectedPrerequisites.Add(new PrerequisiteRow(pre, pre.Level > 1 ? $"{display} (level {pre.Level})" : display, target != null));
        }
        foreach (var r in index.IncomingReferences(ReferenceKind.Entity, s.Name).Where(r => r.Source.EntityType != "ResearchSubject" && r.Source.EntityType != "Player"))
            SelectedUnlocks.Add($"{index.DisplayName(r.Source) ?? r.Source.Name}  ({r.Source.EntityType})");
        foreach (var dependent in Tree.Subjects.Where(d => d.Prerequisites.Any(p => p.Subject.Equals(s.Name, StringComparison.OrdinalIgnoreCase))))
            SelectedUnlocks.Add($"{index.DisplayName(dependent.Entity) ?? dependent.Name}  (research)");
    }

    /// <summary>
    /// Routes an arrow the way the game's arrow image of that shape runs: horizontal links from the right edge of the
    /// prerequisite to the left edge of the dependent, vertical ones between top and bottom edges, combined shapes with one bend.
    /// </summary>
    private ArrowVisual? ArrowFor(ResearchLink link)
    {
        if (Tree.CellOf(link.To) is not { } to) return null;
        var t = new Rect(to.Origin.X, to.Origin.Y, to.Layout.ButtonSize.X, to.Layout.ButtonSize.Y);
        var index = _workspace.Index;
        string tip = $"{index.DisplayName(link.From.Entity) ?? link.From.Name} → {index.DisplayName(link.To.Entity) ?? link.To.Name}" +
                     (link.Level > 1 ? $" (level {link.Level})" : "") + $"\nArrow: {link.Shape}";
        if (link.Shape == ArrowShape.OtherTab || Tree.CellOf(link.From) is not { } from)
        {
            // A short stub into the dependent, standing for a prerequisite in another field.
            var start = new Point(t.Left - 12, t.Top + t.Height / 2);
            var end = new Point(t.Left, start.Y);
            return new ArrowVisual(link, [start, end], Head(start, end), tip + $"\nPrerequisite in {ResearchTree.FieldTitle(link.From.Field ?? "?")}");
        }

        var f = new Rect(from.Origin.X, from.Origin.Y, from.Layout.ButtonSize.X, from.Layout.ButtonSize.Y);
        Point MidLeft(Rect r) => new(r.Left, r.Top + r.Height / 2);
        Point MidRight(Rect r) => new(r.Right, r.Top + r.Height / 2);
        Point MidTop(Rect r) => new(r.Left + r.Width / 2, r.Top);
        Point MidBottom(Rect r) => new(r.Left + r.Width / 2, r.Bottom);

        PointCollection points = link.Shape switch
        {
            ArrowShape.Right1 or ArrowShape.Right2 or ArrowShape.Right3 or ArrowShape.Right4 => [MidRight(f), MidLeft(t)],
            ArrowShape.Down1 or ArrowShape.BlockDown1 => [MidBottom(f), MidTop(t)],
            ArrowShape.Up2 or ArrowShape.BlockUp1 => [MidTop(f), MidBottom(t)],
            ArrowShape.Right1Down1 or ArrowShape.Right1BlockDown1 => [MidRight(f), new Point(MidTop(t).X, MidRight(f).Y), MidTop(t)],
            ArrowShape.Up1Right1 or ArrowShape.Up1Right2 => [MidTop(f), new Point(MidTop(f).X, MidLeft(t).Y), MidLeft(t)],
            _ => [new Point(f.Left + f.Width / 2, f.Top + f.Height / 2), new Point(t.Left + t.Width / 2, t.Top + t.Height / 2)],
        };
        return new ArrowVisual(link, points, Head(points[^2], points[^1]), tip);
    }

    private static PointCollection Head(Point from, Point to)
    {
        var dir = to - from;
        if (dir.Length < 0.1) return [];
        dir.Normalize();
        var normal = new Vector(-dir.Y, dir.X);
        return [to, to - dir * 8 + normal * 4, to - dir * 8 - normal * 4];
    }

    private BitmapSource? Image(string key, Func<BitmapSource?> render)
    {
        if (!_imageCache.TryGetValue(key, out var image)) _imageCache[key] = image = render();
        return image;
    }

    private BitmapSource? Sized(string brush, int width, int height) =>
        _workspace.Textures.RenderBrushSized(brush, width, height) is { } img ? ImageConverter.ToBitmap(img) : null;

    // ---- Editing ----

    /// <summary>Where on the canvas a point falls: block and cell of the selected field, or null.</summary>
    public (ResearchBlock Block, int Column, int Row)? CellAt(Point point)
    {
        if (!Tree.Layouts.TryGetValue(SelectedField, out var layout)) return null;
        foreach (var block in Tree.BlocksOf(SelectedField))
        {
            var (cols, rows) = block.GridSize(layout);
            int col = (int)Math.Floor((point.X - block.Area.X - layout.ButtonOffset.X) / layout.StrideX);
            int row = (int)Math.Floor((point.Y - block.Area.Y - layout.ButtonOffset.Y) / layout.StrideY);
            if (col >= 0 && row >= 0 && col < cols && row < rows) return (block, col, row);
        }
        return null;
    }

    /// <summary>Moves a subject to a cell of the selected field; a subject already there swaps into the old place.</summary>
    public void MoveSubject(string subjectName, ResearchBlock block, int column, int row)
    {
        if (Tree.Subject(subjectName) is not { } subject) return;
        if (subject.Field == block.Field && subject.Block == block.Index && subject.Column == column && subject.Row == row) return;
        var occupant = Tree.Subjects.FirstOrDefault(s => s != subject && s.Field == block.Field && s.Block == block.Index && s.Column == column && s.Row == row);
        Edit(() =>
        {
            if (occupant != null && subject.HasPosition)
                Record(occupant, session => ResearchEdits.Move(session, subject.Field!, subject.Block!.Value, subject.Column!.Value, subject.Row!.Value));
            Record(subject, session => ResearchEdits.Move(session, block.Field, block.Index, column, row));
        });
        StatusMessage = occupant is null ? null : $"Swapped with {_workspace.Index.DisplayName(occupant.Entity) ?? occupant.Name}.";
        SelectByName(subjectName);
    }

    /// <summary>Makes <paramref name="dependent"/> require <paramref name="prerequisite"/>.</summary>
    public void AddLink(string prerequisite, string dependent)
    {
        if (string.Equals(prerequisite, dependent, StringComparison.OrdinalIgnoreCase) || Tree.Subject(dependent) is not { } target) return;
        Edit(() => Record(target, session => ResearchEdits.AddPrerequisite(session, prerequisite)));
        SelectByName(dependent);
    }

    [RelayCommand]
    private void AddPrerequisite()
    {
        if (SelectedSubject is { } s && PrerequisiteToAdd is { } p) AddLink(p.Name, s.Subject.Name);
        PrerequisiteToAdd = null;
    }

    [RelayCommand]
    private void RemovePrerequisite(PrerequisiteRow? row)
    {
        if (row is null || SelectedSubject?.Subject is not { } s) return;
        Edit(() => Record(s, session => ResearchEdits.RemovePrerequisite(session, row.Prerequisite.Subject)));
    }

    [RelayCommand]
    public void DeleteSelectedArrow()
    {
        if (SelectedArrow?.Link is not { } link) return;
        Edit(() => Record(link.To, session => ResearchEdits.RemovePrerequisite(session, link.From.Name)));
        SelectedArrow = null;
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    public void Undo()
    {
        if (!_undo.TryPop(out var session)) return;
        session.Undo();
        _redo.Push(session);
        NotifyUndo();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    public void Redo()
    {
        if (!_redo.TryPop(out var session)) return;
        session.Redo();
        _undo.Push(session);
        NotifyUndo();
    }

    [RelayCommand]
    private void SelectField(string field) => SelectedField = field;

    [RelayCommand]
    private void OpenSelected()
    {
        if (SelectedSubject?.Subject is { } s) _navigate(new EntityTarget(s.Name));
    }

    [RelayCommand]
    private void ShowIssue(IssueRow? issue)
    {
        if (issue?.Subject is { } name) SelectByName(name);
    }

    public void SelectByName(string name)
    {
        if (Tree.Subject(name) is { Field: { } field } subject && Tree.CellOf(subject) != null) SelectedField = field;
        SelectedSubject = Subjects.FirstOrDefault(s => s.Subject.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Runs edits with redraws suspended, then redraws once.</summary>
    private void Edit(Action edits)
    {
        _applying = true;
        try
        {
            edits();
        }
        catch (InvalidOperationException e)
        {
            StatusMessage = e.Message;
        }
        finally
        {
            _applying = false;
        }
        _redo.Clear();
        Tree = Build();
        Refresh();
        NotifyUndo();
    }

    private void Record(ResearchSubject subject, Action<EntityEditSession> edit)
    {
        var session = _workspace.SessionFor(subject.Entity);
        int before = session.UndoCount;
        edit(session);
        if (session.UndoCount > before) _undo.Push(session);
    }

    private void NotifyUndo()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(IsDirty));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }
}
