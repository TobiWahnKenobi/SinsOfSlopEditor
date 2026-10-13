using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SinsEditor.Core;
using SinsEditor.Core.Editing;
using SinsEditor.Core.Index;

namespace SinsEditor.App.Editing;

public enum EntityOperationKind
{
    Duplicate,
    Rename,
    Delete,
}

/// <summary>Asks the shell to run an operation on an entity (sent through the messenger, so any page can ask).</summary>
public sealed record EntityOperationRequest(EntityOperationKind Kind, string EntityName);

public sealed record ChangeRow(string Path, string Description, bool IsDelete);

public sealed record TemplateChoice(EntityRecord Entity, string Display)
{
    public override string ToString() => Display;
}

/// <summary>
/// The dialog for duplicate/new, rename and delete. Every change of an option re-plans the operation,
/// so the preview always shows exactly what applying will write.
/// </summary>
public sealed partial class EntityOperationViewModel : ObservableObject
{
    private readonly ModWorkspace _workspace;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddToRaces), nameof(CanCopyStrings), nameof(UsedByText))]
    private TemplateChoice _source;

    [ObservableProperty]
    private string _newName = "";

    [ObservableProperty]
    private bool _addToRaces = true;

    [ObservableProperty]
    private bool _copyStrings = true;

    [ObservableProperty]
    private bool _restoreLowerLayer;

    [ObservableProperty]
    private bool _removeReferences = true;

    [ObservableProperty]
    private string? _nameError;

    [ObservableProperty]
    private IReadOnlyList<ChangeRow> _changes = [];

    [ObservableProperty]
    private IReadOnlyList<string> _notes = [];

    public EntityOperationViewModel(ModWorkspace workspace, EntityOperationKind kind, EntityRecord entity)
    {
        _workspace = workspace;
        Kind = kind;
        var index = workspace.Index;
        Templates = kind == EntityOperationKind.Duplicate
            ? index.EntitiesIn(EntityCategories.For(entity.EntityType))
                .Select(e => new TemplateChoice(e, $"{index.DisplayName(e) ?? e.Name}  ({e.Name})"))
                .OrderBy(t => t.Display, StringComparer.CurrentCultureIgnoreCase)
                .ToList()
            : [];
        _source = Templates.FirstOrDefault(t => t.Entity.Name == entity.Name) ?? new TemplateChoice(entity, entity.Name);
        _newName = kind switch
        {
            EntityOperationKind.Duplicate => SuggestName(entity.Name + "_Copy"),
            EntityOperationKind.Rename => entity.Name,
            _ => "",
        };
        Replan();
    }

    public EntityOperationKind Kind { get; }
    public IReadOnlyList<TemplateChoice> Templates { get; }
    public ChangeSet? Plan { get; private set; }

    public string Title => Kind switch
    {
        EntityOperationKind.Duplicate => "New entity from a template",
        EntityOperationKind.Rename => $"Rename {Source.Entity.Name}",
        _ => $"Delete {Source.Entity.Name}",
    };

    public string ApplyText => Kind switch
    {
        EntityOperationKind.Duplicate => "Create",
        EntityOperationKind.Rename => "Rename",
        _ => "Delete",
    };

    public bool IsDuplicate => Kind == EntityOperationKind.Duplicate;
    public bool IsDelete => Kind == EntityOperationKind.Delete;
    public bool NeedsName => Kind != EntityOperationKind.Delete;
    public bool CanAddToRaces => IsDuplicate && _workspace.Index.RacesOwning(Source.Entity.Name).Any();
    public bool CanCopyStrings => IsDuplicate && _workspace.Index.OutgoingReferences(Source.Entity).Any(r => r.Kind == ReferenceKind.String && r.Resolved);
    /// <summary>Deleting an override can either restore the lower layer's version or remove the entity.</summary>
    public bool CanRestoreLowerLayer => IsDelete && Source.Entity.File.IsOverride && Source.Entity.File.Layer.IsEditable;
    public bool CanRemoveReferences => IsDelete && !RestoreLowerLayer;
    public string RacesText => string.Join(", ", _workspace.Index.RacesOwning(Source.Entity.Name).Select(r => _workspace.Index.DisplayName(r) ?? r.Name));
    public string UsedByText
    {
        get
        {
            int count = _workspace.Index.IncomingReferences(ReferenceKind.Entity, Source.Entity.Name).Count(r => r.Source.Name != Source.Entity.Name);
            return count == 0 ? "Nothing refers to this entity." : $"{count} references in other entities point to it.";
        }
    }

    public bool CanApply => Plan is { Changes.Count: > 0 } && NameError is null;

    /// <summary>The entity to show after applying: the new or renamed one, or none after a delete.</summary>
    public string? ResultEntity => Kind switch
    {
        EntityOperationKind.Delete => RestoreLowerLayer ? Source.Entity.Name : null,
        _ => NewName.Trim(),
    };

    public event Action<bool>? CloseRequested;

    partial void OnSourceChanged(TemplateChoice value)
    {
        if (IsDuplicate) NewName = SuggestName(value.Entity.Name + "_Copy");
        Replan();
    }

    partial void OnNewNameChanged(string value) => Replan();
    partial void OnAddToRacesChanged(bool value) => Replan();
    partial void OnCopyStringsChanged(bool value) => Replan();
    partial void OnRemoveReferencesChanged(bool value) => Replan();

    partial void OnRestoreLowerLayerChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRemoveReferences));
        Replan();
    }

    [RelayCommand]
    private void Apply()
    {
        if (CanApply) CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    private void Replan()
    {
        string name = NewName.Trim();
        NameError = Kind switch
        {
            EntityOperationKind.Rename when name == Source.Entity.Name => "Enter a new name.",
            EntityOperationKind.Delete => null,
            _ => EntityOperations.ValidateNewName(_workspace, name),
        };
        Plan = NameError != null ? null : Kind switch
        {
            EntityOperationKind.Duplicate => EntityOperations.PlanDuplicate(_workspace, Source.Entity, name, AddToRaces && CanAddToRaces, CopyStrings && CanCopyStrings),
            EntityOperationKind.Rename => EntityOperations.PlanRename(_workspace, Source.Entity, name),
            _ => EntityOperations.PlanDelete(_workspace, Source.Entity,
                RestoreLowerLayer ? DeleteMode.RestoreLowerLayer : DeleteMode.RemoveEntity, RemoveReferences && !RestoreLowerLayer),
        };
        Changes = Plan?.Changes
            .OrderBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(c => new ChangeRow(c.RelativePath, c.Description, c.IsDelete))
            .ToList() ?? [];
        Notes = Plan?.Notes ?? [];
        OnPropertyChanged(nameof(CanApply));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private string SuggestName(string baseName)
    {
        string name = baseName;
        for (int n = 2; EntityOperations.ValidateNewName(_workspace, name) != null; n++) name = $"{baseName}{n}";
        return name;
    }
}
