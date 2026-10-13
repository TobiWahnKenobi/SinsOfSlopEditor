using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SinsEditor.App.Imaging;
using SinsEditor.App.Navigation;
using SinsEditor.App.ViewModels;
using SinsEditor.Core;
using SinsEditor.Core.FileSystem;
using SinsEditor.Core.Index;
using SinsEditor.Core.Textures;

namespace SinsEditor.App.Assets;

public sealed class AssetRow(string name, ResolvedFile file, int usedBy, NavTarget target, Func<BitmapSource?>? preview)
{
    private BitmapSource? _preview;
    private bool _loaded;

    public string Name { get; } = name;
    public string File { get; } = file.RelativePath;
    public string Layer { get; } = file.Layer.Name;
    public bool InMod { get; } = file.Layer.IsEditable;
    public int UsedBy { get; } = usedBy;
    public NavTarget Target { get; } = target;

    /// <summary>A small thumbnail, decoded when the row first shows.</summary>
    public BitmapSource? Preview
    {
        get
        {
            if (!_loaded && preview != null) { _preview = preview(); _loaded = true; }
            return _preview;
        }
    }
}

/// <summary>All meshes or all textures of the effective mod, with search and a filter on the mod's own files.</summary>
public sealed partial class AssetListViewModel : PageViewModel
{
    private readonly Action<NavTarget> _navigate;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _onlyMod;

    public AssetListViewModel(ModWorkspace workspace, ReferenceKind kind, Action<NavTarget> navigate)
    {
        _navigate = navigate;
        Kind = kind;
        var index = workspace.Index;
        // Textures are used by brushes too: count each brush once per texture.
        var brushUses = kind == ReferenceKind.Texture
            ? index.Brushes.Entries.Values
                .SelectMany(e => Brush.FromEntry(e).Images.Values.Select(i => Path.GetFileNameWithoutExtension(i.FileName)).Distinct(StringComparer.OrdinalIgnoreCase))
                .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase)
            : [];
        int Uses(string name) => index.IncomingReferences(kind, name).Count + brushUses.GetValueOrDefault(name);
        Items = kind == ReferenceKind.Mesh
            ? index.Meshes.Select(kv => new AssetRow(kv.Key, kv.Value, Uses(kv.Key) + Uses(kv.Key + ".mesh"), new MeshTarget(kv.Key), null))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList()
            : index.Textures.Select(kv => new AssetRow(kv.Key, kv.Value, Uses(kv.Key), new TextureTarget(kv.Key), () => Thumbnail(kv.Value)))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is AssetRow r && (!OnlyMod || r.InMod)
            && (SearchText.Length == 0 || r.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) || r.File.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
    }

    public ReferenceKind Kind { get; }
    public bool ShowPreview => Kind == ReferenceKind.Texture;
    public override string Title => Kind == ReferenceKind.Mesh ? "Meshes" : "Textures";
    public IReadOnlyList<AssetRow> Items { get; }
    public ICollectionView View { get; }
    public string CountText => $"{View.Cast<object>().Count():N0} of {Items.Count:N0}";

    partial void OnSearchTextChanged(string value) => Refresh();
    partial void OnOnlyModChanged(bool value) => Refresh();

    private void Refresh()
    {
        View.Refresh();
        OnPropertyChanged(nameof(CountText));
    }

    private static BitmapSource? Thumbnail(ResolvedFile file)
    {
        try
        {
            var image = TextureLoader.Load(file.AbsolutePath);
            double scale = Math.Min(1.0, 64.0 / Math.Max(image.Width, image.Height));
            if (scale < 1) image = ImageOps.Scale(image, Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)));
            return ImageConverter.ToBitmap(image);
        }
        catch (Exception e) when (e is IOException or TextureLoadException)
        {
            return null;
        }
    }

    [RelayCommand]
    private void Open(AssetRow? row)
    {
        if (row != null) _navigate(row.Target);
    }
}
