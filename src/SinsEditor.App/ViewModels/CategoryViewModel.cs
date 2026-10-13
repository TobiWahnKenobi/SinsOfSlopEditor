using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SinsEditor.App.Imaging;
using SinsEditor.App.Navigation;
using SinsEditor.Core;
using SinsEditor.Core.Documents;
using SinsEditor.Core.Index;

namespace SinsEditor.App.ViewModels;

public sealed class EntityRow(EntityRecord entity, string? displayName, string races, Func<BitmapSource?> icon)
{
    private BitmapSource? _icon;
    private bool _iconLoaded;

    public string Name => entity.Name;
    public string DisplayName { get; } = displayName ?? entity.Name;
    public string EntityType => entity.EntityType;
    public string Layer => entity.File.IsOverride ? $"{entity.File.Layer.Name} (override)" : entity.File.Layer.Name;
    public string Races { get; } = races;
    public EntityTarget Target { get; } = new(entity.Name);

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

/// <summary>List of all entities in a category. Filters arrive in M3; for now a text search.</summary>
public sealed partial class CategoryViewModel : PageViewModel
{
    private readonly EntityCategory _category;
    private readonly Action<NavTarget> _navigate;

    [ObservableProperty]
    private string _searchText = "";

    public CategoryViewModel(ModWorkspace workspace, EntityCategory category, Action<NavTarget> navigate)
    {
        _category = category;
        _navigate = navigate;
        var index = workspace.Index;
        Items = index.EntitiesIn(category)
            .Select(e => new EntityRow(e, index.DisplayName(e),
                string.Join(", ", index.RacesOwning(e.Name).Select(r => index.DisplayName(r) ?? r.Name)),
                () => ImageConverter.RenderBrush(workspace.Textures, HudIcon(e))))
            .OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is EntityRow r && Matches(r);
    }

    public override string Title => _category.Name;
    public IReadOnlyList<EntityRow> Items { get; }
    public ICollectionView View { get; }
    public string CountText => $"{View.Cast<object>().Count():N0} of {Items.Count:N0}";

    partial void OnSearchTextChanged(string value)
    {
        View.Refresh();
        OnPropertyChanged(nameof(CountText));
    }

    [RelayCommand]
    private void Open(EntityRow? row)
    {
        if (row != null) _navigate(row.Target);
    }

    private bool Matches(EntityRow row) =>
        SearchText.Length == 0
        || row.DisplayName.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
        || row.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
        || row.Races.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase);

    internal static string? HudIcon(EntityRecord entity) =>
        entity.Document.Lines.FirstOrDefault(l => l.Key is "hudIcon" or "smallHudIcon") is { } line
        && SinsValue.TryGetString(line.Value, out var name) ? name : null;
}
