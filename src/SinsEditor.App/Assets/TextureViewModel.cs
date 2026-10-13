using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SinsEditor.App.Imaging;
using SinsEditor.App.Navigation;
using SinsEditor.App.ViewModels;
using SinsEditor.Core;
using SinsEditor.Core.Index;
using SinsEditor.Core.Textures;

namespace SinsEditor.App.Assets;

/// <summary>A texture at full size or fitted, with its format, and the brushes and entities that use it.</summary>
public sealed partial class TextureViewModel : PageViewModel
{
    private readonly Action<NavTarget> _navigate;

    [ObservableProperty]
    private BitmapSource? _image;

    [ObservableProperty]
    private string? _status = "Loading…";

    [ObservableProperty]
    private string? _infoText;

    [ObservableProperty]
    private bool _actualSize;

    public TextureViewModel(ModWorkspace workspace, string name, Action<NavTarget> navigate)
    {
        _navigate = navigate;
        Name = Path.GetFileNameWithoutExtension(name);
        var index = workspace.Index;
        var brushes = index.Brushes.Entries.Values
            .Where(e => Brush.FromEntry(e).Images.Values.Any(i => string.Equals(Path.GetFileNameWithoutExtension(i.FileName), Name, StringComparison.OrdinalIgnoreCase)))
            .Select(e => new UsageRow(e.Name, $"brush · {Path.GetFileName(e.File.RelativePath)}", new BrushTarget(e.Name)));
        var entities = index.IncomingReferences(ReferenceKind.Texture, Name)
            .Select(r => new UsageRow(index.DisplayName(r.Source) ?? r.Source.Name, $"{r.Source.Name} · {r.Key}", new EntityTarget(r.Source.Name, r.LineIndex)));
        UsedBy = entities.Concat(brushes).ToList();
        _ = LoadAsync(workspace);
    }

    public string Name { get; }
    public override string Title => Name;
    public override string? Breadcrumb => "Textures";
    public IReadOnlyList<UsageRow> UsedBy { get; }
    public bool HasNoUsedBy => UsedBy.Count == 0;

    private async Task LoadAsync(ModWorkspace workspace)
    {
        if (!workspace.Index.Textures.TryGetValue(Name, out var file))
        {
            Status = $"No layer has a texture named {Name}.";
            return;
        }
        try
        {
            var (image, format) = await Task.Run(() => (TextureLoader.Load(file.AbsolutePath), TextureFormats.Detect(file.AbsolutePath)));
            Image = ImageConverter.ToBitmap(image);
            InfoText = $"{image.Width} × {image.Height} · {format?.ToString() ?? Path.GetExtension(file.RelativePath).TrimStart('.').ToUpperInvariant()} · " +
                       $"{file.RelativePath} · {file.Layer.Name}";
            Status = null;
        }
        catch (Exception e) when (e is IOException or TextureLoadException)
        {
            Status = $"Couldn't read the texture: {e.Message}";
        }
    }

    [RelayCommand]
    private void Open(NavTarget? target)
    {
        if (target != null) _navigate(target);
    }
}
