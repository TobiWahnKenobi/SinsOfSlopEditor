using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using SinsEditor.App.Imaging;
using SinsEditor.App.Navigation;
using SinsEditor.Core;
using SinsEditor.Core.Documents;
using SinsEditor.Core.Index;

namespace SinsEditor.App.ViewModels;

public sealed class EntityRow(EntityRecord entity, string? displayName, IReadOnlyDictionary<string, IReadOnlyList<string>> facetValues,
    bool hasProblems, Func<BitmapSource?> icon)
{
    private BitmapSource? _icon;
    private bool _iconLoaded;

    public EntityRecord Entity => entity;
    public string Name => entity.Name;
    public string DisplayName { get; } = displayName ?? entity.Name;
    public string EntityType => entity.EntityType;
    public string Layer => entity.File.IsOverride ? $"{entity.File.Layer.Name} (override)" : entity.File.Layer.Name;
    public string Races => this[Facets.RaceId];
    public bool HasProblems { get; } = hasProblems;
    public EntityTarget Target { get; } = new(entity.Name);
    public IReadOnlyDictionary<string, IReadOnlyList<string>> FacetValues { get; } = facetValues;

    /// <summary>Facet values joined for display; used by the generated columns (<c>[facetId]</c>).</summary>
    public string this[string facetId] => FacetValues.TryGetValue(facetId, out var v) ? string.Join(", ", v) : "";

    /// <summary>Rendered on first access, so only visible rows decode textures.</summary>
    public BitmapSource? Icon
    {
        get
        {
            if (!_iconLoaded) { _icon = icon(); _iconLoaded = true; }
            return _icon;
        }
    }
}

/// <summary>All entities of a category with search and filters. Filters come from <see cref="Facets"/>.</summary>
public sealed partial class CategoryViewModel : PageViewModel
{
    /// <summary>More filters than columns: extra columns squeeze the names.</summary>
    private const int MaxPropertyColumns = 3;

    private readonly EntityCategory _category;
    private readonly Action<NavTarget> _navigate;

    [ObservableProperty]
    private string _searchText = "";

    public CategoryViewModel(ModWorkspace workspace, EntityCategory category, Action<NavTarget> navigate)
    {
        _category = category;
        _navigate = navigate;
        var index = workspace.Index;
        var entities = index.EntitiesIn(category).ToList();
        var facets = Facets.For(index, entities, workspace.HasProblems);

        Items = entities
            .Select(e => new EntityRow(e, index.DisplayName(e),
                facets.ToDictionary(f => f.Id, f => f.ValuesOf(e)),
                workspace.HasProblems(e),
                () => ImageConverter.RenderBrush(workspace.Textures, HudIcon(e))))
            .OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        Filters = facets
            .Select(f => new FacetFilterViewModel(f, Items.Select(r => r.FacetValues[f.Id]), Refresh))
            .Where(f => f.Values.Count > 1)
            .ToList();
        PropertyColumns = facets.Where(f => !f.Id.StartsWith('#')).Take(MaxPropertyColumns).Select(f => (f.Id, f.Title)).ToList();

        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is EntityRow r && Matches(r);
    }

    public override string Title => _category.Name;
    public IReadOnlyList<EntityRow> Items { get; }
    public IReadOnlyList<FacetFilterViewModel> Filters { get; }
    /// <summary>Facets found in the data, shown as extra columns.</summary>
    public IReadOnlyList<(string Id, string Title)> PropertyColumns { get; }
    public ICollectionView View { get; }
    public string CountText => $"{View.Cast<object>().Count():N0} of {Items.Count:N0}";
    public bool HasActiveFilters => Filters.Any(f => f.HasSelection) || SearchText.Length > 0;

    partial void OnSearchTextChanged(string value) => Refresh();

    [RelayCommand]
    private void ClearFilters()
    {
        foreach (var f in Filters) f.Clear();
        SearchText = "";
    }

    [ObservableProperty]
    private EntityRow? _selectedRow;

    /// <summary>New entity from a template: the selected row, or the first one.</summary>
    [RelayCommand]
    private void New()
    {
        var template = SelectedRow ?? View.Cast<EntityRow>().FirstOrDefault() ?? Items.FirstOrDefault();
        if (template != null)
            WeakReferenceMessenger.Default.Send(
                new Editing.EntityOperationRequest(Editing.EntityOperationKind.Duplicate, template.Name));
    }

    [RelayCommand]
    private void Open(EntityRow? row)
    {
        if (row != null) _navigate(row.Target);
    }

    private void Refresh()
    {
        View.Refresh();
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HasActiveFilters));
    }

    private bool Matches(EntityRow row) =>
        Filters.All(f => f.Matches(row.FacetValues[f.Facet.Id]))
        && (SearchText.Length == 0
            || row.DisplayName.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            || row.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || row.Races.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase));

    internal static string? HudIcon(EntityRecord entity) =>
        entity.Document.Lines.FirstOrDefault(l => l.Key is "hudIcon" or "smallHudIcon") is { } line
        && SinsValue.TryGetString(line.Value, out var name) ? name : null;
}
