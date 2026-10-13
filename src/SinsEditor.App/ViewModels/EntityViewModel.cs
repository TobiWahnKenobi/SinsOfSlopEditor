using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using SinsEditor.App.Editing;
using SinsEditor.App.Imaging;
using SinsEditor.App.Navigation;
using SinsEditor.Core;
using SinsEditor.Core.Editing;
using SinsEditor.Core.Index;

namespace SinsEditor.App.ViewModels;

public sealed record ReferenceRow(string Key, string Kind, string Target, string? TargetDisplayName, bool Resolved, EntityTarget? NavTarget, int Line)
{
    public string Text => TargetDisplayName is null || TargetDisplayName == Target ? Target : $"{TargetDisplayName}  ({Target})";
}

public sealed record ReferenceGroup(string Title, IReadOnlyList<ReferenceRow> Rows);

/// <summary>A problem shown in the entity's banner; <paramref name="LineIndex"/> is null when it isn't about a line.</summary>
public sealed record EntityProblemRow(Core.Validation.ProblemSeverity Severity, string Title, string Message, int? LineIndex)
{
    public bool HasLine => LineIndex != null;
    public string LineText => LineIndex is { } line ? $"line {line + 1}" : "";
}

/// <summary>
/// Entity page: form editor, connected items both ways, and raw text. Edits live in the workspace's edit session
/// for the entity, so every page showing it shares them and they survive navigating away.
/// </summary>
public sealed partial class EntityViewModel : PageViewModel
{
    private readonly ModWorkspace _workspace;
    private readonly Action<NavTarget> _navigate;

    [ObservableProperty]
    private string _displayName = "";

    [ObservableProperty]
    private BitmapSource? _icon;

    [ObservableProperty]
    private string _layerText = "";

    [ObservableProperty]
    private string? _overrideNotice;

    [ObservableProperty]
    private IReadOnlyList<ReferenceRow> _races = [];

    [ObservableProperty]
    private IReadOnlyList<ReferenceGroup> _uses = [];

    [ObservableProperty]
    private IReadOnlyList<ReferenceGroup> _usedBy = [];

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems), nameof(ProblemSummary), nameof(ProblemsAreErrors))]
    private IReadOnlyList<EntityProblemRow> _problems = [];

    public bool HasProblems => Problems.Count > 0;
    public bool ProblemsAreErrors => Problems.Any(p => p.Severity == Core.Validation.ProblemSeverity.Error);

    public string ProblemSummary
    {
        get
        {
            int errors = Problems.Count(p => p.Severity == Core.Validation.ProblemSeverity.Error);
            int warnings = Problems.Count(p => p.Severity == Core.Validation.ProblemSeverity.Warning);
            int notes = Problems.Count - errors - warnings;
            string total = Problems.Count == 1 ? "1 problem" : $"{Problems.Count} problems";
            var parts = new[] { (errors, "error", "errors"), (warnings, "warning", "warnings"), (notes, "note", "notes") }
                .Where(p => p.Item1 > 0).Select(p => $"{p.Item1} {(p.Item1 == 1 ? p.Item2 : p.Item3)}").ToList();
            return parts.Count > 1 ? $"{total} ({string.Join(", ", parts)})" : total;
        }
    }

    /// <summary>A line to show once the view is up (navigation to an entity at a line).</summary>
    public int? PendingLine { get; set; }

    /// <summary>Asks the view to show a line in the form (switch tab, clear the filter, scroll, select).</summary>
    public event Action<int>? ShowLineRequested;

    [RelayCommand]
    private void ShowProblem(EntityProblemRow? row)
    {
        if (row?.LineIndex is { } line) ShowLineRequested?.Invoke(line);
    }

    [ObservableProperty]
    private bool _isSaving;

    public EntityViewModel(ModWorkspace workspace, EntityRecord entity, Action<NavTarget> navigate)
    {
        _workspace = workspace;
        _navigate = navigate;
        Session = workspace.SessionFor(entity);
        Editor = new PropertyEditorViewModel(workspace, Session, CandidateProvider.For(workspace), navigate);
        Session.Changed += OnSessionChanged;
        LoadInfo();
    }

    public EntityEditSession Session { get; }
    public PropertyEditorViewModel Editor { get; }
    public EntityRecord Entity => Session.Original;
    public EntityCategory Category => EntityCategories.For(Entity.EntityType);
    public override string Title => DisplayName;
    public override string? Breadcrumb => Category.Name;
    public override bool IsDirty => Session.IsDirty;

    public string Subtitle => $"{Entity.Name} · {Entity.EntityType} · {Entity.File.RelativePath}";
    public bool HasNoUsedBy => UsedBy.Count == 0;
    public bool CanUndo => Session.CanUndo;
    public bool CanRedo => Session.CanRedo;
    public string UndoToolTip => Session.UndoDescription is { } d ? $"Undo {d} (Ctrl+Z)" : "Undo (Ctrl+Z)";
    public string RedoToolTip => Session.RedoDescription is { } d ? $"Redo {d} (Ctrl+Y)" : "Redo (Ctrl+Y)";

    public IReadOnlyList<SaveFormat> SaveFormats { get; } = [SaveFormat.Text, SaveFormat.Binary];

    public SaveFormat SelectedFormat
    {
        get => Session.Format ?? _workspace.DefaultSaveFormat(Entity);
        set
        {
            Session.Format = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Raised when the document changed outside the raw text editor, so the view reloads its text.</summary>
    public event Action? TextReloadRequested;

    /// <summary>Set by the view while the raw text editor has unapplied changes; called before saving.</summary>
    public Action? FlushRawText { get; set; }

    public void ApplyRawText(string text) => Session.ReplaceText(text);

    [RelayCommand(CanExecute = nameof(CanUndo))]
    public void Undo() => Session.Undo();

    [RelayCommand(CanExecute = nameof(CanRedo))]
    public void Redo() => Session.Redo();

    [RelayCommand]
    public async Task SaveAsync()
    {
        FlushRawText?.Invoke();
        if (!Session.IsDirty && SelectedFormat == _workspace.DefaultSaveFormat(Entity) && Entity.File.Layer.IsEditable) return;
        IsSaving = true;
        StatusMessage = null;
        try
        {
            await Services.Busy.Current.RunAsync($"Saving {Entity.Name}…", _ => _workspace.SaveAsync(Session, SelectedFormat));
            StatusMessage = $"Saved to {_workspace.ModName}/{Entity.File.RelativePath}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Core.Conversion.ConvertDataException)
        {
            StatusMessage = $"Couldn't save: {e.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private void RequestOperation(EntityOperationKind kind) =>
        WeakReferenceMessenger.Default.Send(new EntityOperationRequest(kind, Entity.Name));

    [RelayCommand]
    private void Open(ReferenceRow? row)
    {
        if (row?.NavTarget != null) _navigate(row.NavTarget);
    }

    public override void Detach()
    {
        Session.Changed -= OnSessionChanged;
        Editor.Detach();
    }

    private void OnSessionChanged(EditKind kind)
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoToolTip));
        OnPropertyChanged(nameof(RedoToolTip));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        TextReloadRequested?.Invoke();
        if (kind == EditKind.Saved) LoadInfo();
    }

    /// <summary>Header and connected items from the index (they reflect the last saved state).</summary>
    private void LoadInfo()
    {
        var index = _workspace.Index;
        var entity = Entity;
        DisplayName = index.DisplayName(entity) ?? entity.Name;
        Icon = ImageConverter.RenderBrush(_workspace.Textures, CategoryViewModel.HudIcon(entity));

        string layer = entity.File.Layer.Name;
        LayerText = entity.File.IsOverride
            ? $"{layer}, overrides {string.Join(", ", entity.File.OverriddenLayers.Select(l => l.Name))}"
            : layer;
        if (!entity.InManifest) LayerText += " · not in entity.manifest";
        OverrideNotice = entity.File.Layer.IsEditable
            ? null
            : $"This entity comes from {layer}. Saving puts a copy into {_workspace.ModName}, which then overrides it.";

        Races = index.RacesOwning(entity.Name)
            .Select(r => new ReferenceRow("", "Race", r.Name, index.DisplayName(r), true, new EntityTarget(r.Name), -1))
            .ToList();

        Uses = index.OutgoingReferences(entity)
            .GroupBy(r => r.Kind)
            .OrderBy(g => g.Key)
            .Select(g => new ReferenceGroup(GroupTitle(g.Key, g.Count()), g
                .Select(r => new ReferenceRow(r.Key, r.Kind.ToString(), r.Target, TargetName(index, r.Kind, r.Target), r.Resolved,
                    r.Kind == ReferenceKind.Entity && r.Resolved ? new EntityTarget(r.Target) : null, r.LineIndex + 1))
                .ToList()))
            .ToList();

        UsedBy = index.IncomingReferences(ReferenceKind.Entity, entity.Name)
            .GroupBy(r => EntityCategories.For(r.Source.EntityType).Name)
            .OrderBy(g => g.Key)
            .Select(g => new ReferenceGroup($"{g.Key} · {g.Count()}", g
                .Select(r => new ReferenceRow(r.Key, r.Source.EntityType, r.Source.Name, index.DisplayName(r.Source), true,
                    new EntityTarget(r.Source.Name), r.LineIndex + 1))
                .ToList()))
            .ToList();

        // Messages start with the entity's name, which is redundant on its own page.
        string prefix = entity.Name + ": ";
        var problems = _workspace.ProblemsOf(entity);
        Problems = problems
            .Select(p => new EntityProblemRow(p.Severity, Core.Validation.ProblemCodes.Title(p.Code),
                p.Message.StartsWith(prefix, StringComparison.Ordinal) ? p.Message[prefix.Length..] : p.Message, p.LineIndex))
            .ToList();
        Editor.SetProblems(problems);

        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(HasNoUsedBy));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SelectedFormat));
    }

    private static string GroupTitle(ReferenceKind kind, int count) => kind switch
    {
        ReferenceKind.Entity => "Entities",
        ReferenceKind.String => "Strings",
        ReferenceKind.Brush => "Brushes",
        ReferenceKind.Mesh => "Meshes",
        ReferenceKind.Particle => "Particles",
        ReferenceKind.Texture => "Textures",
        ReferenceKind.Sound => "Sounds",
        _ => kind.ToString(),
    } + $" · {count}";

    private static string? TargetName(ModIndex index, ReferenceKind kind, string target) => kind switch
    {
        ReferenceKind.Entity when index.Entities.TryGetValue(target, out var e) => index.DisplayName(e),
        ReferenceKind.String => index.Strings.Get(target) is { } s ? $"“{Truncate(s, 60)}”" : null,
        _ => null,
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
