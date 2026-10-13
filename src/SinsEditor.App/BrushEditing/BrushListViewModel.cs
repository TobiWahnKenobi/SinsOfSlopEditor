using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SinsEditor.App.Imaging;
using SinsEditor.App.Navigation;
using SinsEditor.App.ViewModels;
using SinsEditor.Core;
using SinsEditor.Core.Textures;

namespace SinsEditor.App.BrushEditing;

public sealed class BrushRow(string name, string content, string texture, string file, string layer, Func<BitmapSource?> preview)
{
    private BitmapSource? _preview;
    private bool _loaded;

    public string Name { get; } = name;
    public string Content { get; } = content;
    public string Texture { get; } = texture;
    public string File { get; } = file;
    public string Layer { get; } = layer;

    public BitmapSource? Preview
    {
        get
        {
            if (!_loaded) { _preview = preview(); _loaded = true; }
            return _preview;
        }
    }
}

/// <summary>Every brush of the effective mod, with preview, search and a filter on the edited mod's own brushes.</summary>
public sealed partial class BrushListViewModel : PageViewModel
{
    private readonly Action<NavTarget> _navigate;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _onlyMod;

    [ObservableProperty]
    private BrushRow? _selectedRow;

    public BrushListViewModel(ModWorkspace workspace, Action<NavTarget> navigate)
    {
        _navigate = navigate;
        Items = workspace.Index.Brushes.Entries.Values
            .Select(e =>
            {
                var brush = Brush.FromEntry(e);
                return new BrushRow(e.Name, brush.Content, brush.PreviewImage?.FileName ?? "", Path.GetFileName(e.File.RelativePath),
                    e.File.Layer.Name, () => ImageConverter.RenderBrush(workspace.Textures, e.Name));
            })
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ModLayer = workspace.Stack.EditedMod?.Name ?? "";
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is BrushRow r && Matches(r);
    }

    public override string Title => "Brushes";
    public IReadOnlyList<BrushRow> Items { get; }
    public ICollectionView View { get; }
    public string ModLayer { get; }
    public string CountText => $"{View.Cast<object>().Count():N0} of {Items.Count:N0}";

    partial void OnSearchTextChanged(string value) => Refresh();
    partial void OnOnlyModChanged(bool value) => Refresh();

    private void Refresh()
    {
        View.Refresh();
        OnPropertyChanged(nameof(CountText));
    }

    private bool Matches(BrushRow r) =>
        (!OnlyMod || r.Layer == ModLayer)
        && (SearchText.Length == 0
            || r.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || r.Texture.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || r.File.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private void Open(BrushRow? row)
    {
        if (row != null) _navigate(new BrushTarget(row.Name));
    }

    [RelayCommand]
    private void New() => _navigate(new BrushTarget(null));
}
