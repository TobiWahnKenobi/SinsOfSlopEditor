using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SinsEditor.App.Editing;
using SinsEditor.App.Imaging;
using SinsEditor.App.Navigation;
using SinsEditor.App.ViewModels;
using SinsEditor.Core;
using SinsEditor.Core.Textures;

namespace SinsEditor.App.BrushEditing;

/// <summary>One image of the brush being edited: what it is now, and what it will be.</summary>
public sealed partial class StateSlot(string state, BitmapSource? current, PixelImage? currentPixels) : ObservableObject
{
    [ObservableProperty]
    private PixelImage? _imported;

    [ObservableProperty]
    private string? _importedFile;

    [ObservableProperty]
    private BitmapSource? _result;

    [ObservableProperty]
    private string _source = "";

    /// <summary>Empty for Simple brushes.</summary>
    public string State { get; } = state;
    public string Title => State.Length == 0 ? "Image" : State;
    public BitmapSource? Current { get; } = current;
    public PixelImage? CurrentPixels { get; } = currentPixels;
    public bool IsNormal => State is "" or BrushStates.Normal;
}

public enum TextureChoice
{
    Keep,
    Existing,
    New,
}

/// <summary>
/// Edit an existing brush or create a new one: import images from disk, choose size, fit and atlas, preview the packed atlas
/// and every file change, then apply. Packing and writing are done by <see cref="BrushEditor"/>.
/// </summary>
public sealed partial class BrushEditorViewModel : PageViewModel
{
    private const string NewFileOption = "(new file)";
    private readonly ModWorkspace _workspace;
    private readonly Action<NavTarget> _navigate;
    private readonly Func<Task> _reload;
    private CancellationTokenSource? _planCts;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _contentKind = "States";
    [ObservableProperty] private string _brushesFile = "";
    [ObservableProperty] private string _newBrushesFile = "";
    [ObservableProperty] private int _width;
    [ObservableProperty] private int _height;
    [ObservableProperty] private FitMode _fitMode = FitMode.Fit;
    [ObservableProperty] private bool _deriveStates = true;
    [ObservableProperty] private TextureChoice _textureChoice;
    [ObservableProperty] private string? _otherTexture;
    [ObservableProperty] private string _newTexture = "";
    [ObservableProperty] private TextureFileFormat _newTextureFormat = TextureFileFormat.Tga32;
    [ObservableProperty] private BitmapSource? _atlasPreview;
    [ObservableProperty] private string? _planSummary;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private IReadOnlyList<ChangeRow> _changes = [];
    [ObservableProperty] private IReadOnlyList<string> _notes = [];
    [ObservableProperty] private bool _isBusy;

    private BrushEditPlan? _plan;

    public BrushEditorViewModel(ModWorkspace workspace, string? brushName, Action<NavTarget> navigate, Func<Task> reload)
    {
        _workspace = workspace;
        _navigate = navigate;
        _reload = reload;
        var index = workspace.Index;
        var mod = workspace.Stack.EditedMod!;

        BrushesFiles = [.. mod.Files.Keys.Where(p => p.StartsWith("Window/", StringComparison.OrdinalIgnoreCase)
            && p.EndsWith(".brushes", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase), NewFileOption];
        Textures = index.Textures.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();

        if (brushName != null && index.Brushes.Entries.TryGetValue(brushName, out var entry))
        {
            Brush = Brush.FromEntry(entry);
            Name = Brush.Name;
            ContentKind = Brush.Images.ContainsKey(Core.Textures.Brush.SimpleState) ? "Simple" : "States";
            BrushesFile = entry.File.RelativePath;
            var preview = Brush.PreviewImage;
            Width = preview?.Box.Width ?? 0;
            Height = preview?.Box.Height ?? 0;
            CurrentTexture = preview?.FileName;
            TextureChoice = CurrentTexture != null && index.Textures.ContainsKey(Path.GetFileNameWithoutExtension(CurrentTexture)) ? TextureChoice.Keep : TextureChoice.New;
            DeriveStates = false;
        }
        else
        {
            Name = "";
            BrushesFile = BrushesFiles[0];
            TextureChoice = TextureChoice.New;
            NewTexture = $"{workspace.ModName}_Icons";
            if (index.Textures.ContainsKey(NewTexture))
            {
                TextureChoice = TextureChoice.Existing;
                OtherTexture = NewTexture;
            }
        }
        NewBrushesFile = $"{workspace.ModName}.brushes";
        RebuildSlots();
        _ = ReplanAsync();
    }

    public Brush? Brush { get; }
    public bool IsNew => Brush is null;
    public string? CurrentTexture { get; }
    public override string Title => IsNew ? "New brush" : Brush!.Name;
    public override string? Breadcrumb => "Brushes";
    public IReadOnlyList<string> ContentKinds { get; } = ["Simple", "States"];
    public IReadOnlyList<string> BrushesFiles { get; }
    public IReadOnlyList<string> Textures { get; }
    public IReadOnlyList<FitMode> FitModes { get; } = [FitMode.Fit, FitMode.Fill, FitMode.Stretch];
    public IReadOnlyList<TextureFileFormat> NewTextureFormats { get; } = [TextureFileFormat.Tga32, TextureFileFormat.DdsDxt5];
    public ObservableCollection<StateSlot> Slots { get; } = [];
    public bool IsNewFile => BrushesFile == NewFileOption;
    public string KeepTextureText
    {
        get
        {
            if (CurrentTexture is null) return "Keep current texture";
            int count = Brush!.Images.Values.Select(i => i.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return count > 1 ? $"Keep current textures ({CurrentTexture} and {count - 1} more, one per state)" : $"Keep current texture ({CurrentTexture})";
        }
    }
    public bool CanKeepTexture => !IsNew && CurrentTexture != null;
    public bool CanApply => _plan != null && Error is null && !IsBusy;

    partial void OnContentKindChanged(string value) { if (IsNew) RebuildSlots(); Changed(); }
    partial void OnBrushesFileChanged(string value) { OnPropertyChanged(nameof(IsNewFile)); Changed(); }
    partial void OnNewBrushesFileChanged(string value) => Changed();
    partial void OnNameChanged(string value) => Changed();
    partial void OnWidthChanged(int value) => Changed();
    partial void OnHeightChanged(int value) => Changed();
    partial void OnFitModeChanged(FitMode value) => Changed();
    partial void OnDeriveStatesChanged(bool value) => Changed();
    partial void OnTextureChoiceChanged(TextureChoice value) => Changed();
    partial void OnOtherTextureChanged(string? value) => Changed();
    partial void OnNewTextureChanged(string value) => Changed();
    partial void OnNewTextureFormatChanged(TextureFileFormat value) => Changed();

    private void Changed() => _ = ReplanAsync();

    private void RebuildSlots()
    {
        Slots.Clear();
        // Normal first, so it can be imported before the states derived from it.
        List<string> states = ContentKind == "Simple" ? [Core.Textures.Brush.SimpleState] : BrushStates.All.Reverse().ToList();
        foreach (string state in states)
        {
            PixelImage? pixels = null;
            if (Brush != null && Brush.Images.ContainsKey(state))
                pixels = _workspace.Textures.RenderBrush(Brush.Name, state.Length == 0 ? null : state).Image;
            Slots.Add(new StateSlot(state, pixels is { Width: > 0 } ? ImageConverter.ToBitmap(pixels) : null, pixels));
        }
    }

    [RelayCommand]
    private void ImportImage(StateSlot? slot)
    {
        if (slot is null) return;
        var dialog = new OpenFileDialog { Filter = ImageFiles.DialogFilter, Title = $"Image for {slot.Title}" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            slot.Imported = ImageFiles.Load(dialog.FileName);
            slot.ImportedFile = Path.GetFileName(dialog.FileName);
            if (Width <= 0 || Height <= 0)
            {
                Width = slot.Imported.Width;
                Height = slot.Imported.Height;
            }
            if (slot.IsNormal && Brush != null) DeriveStates = true;
            Changed();
        }
        catch (Exception e) when (e is IOException or NotSupportedException or TextureLoadException or System.Runtime.InteropServices.COMException)
        {
            Error = $"Couldn't read {Path.GetFileName(dialog.FileName)}: {e.Message}";
        }
    }

    [RelayCommand]
    private void ClearImage(StateSlot? slot)
    {
        if (slot is null) return;
        slot.Imported = null;
        slot.ImportedFile = null;
        Changed();
    }

    /// <summary>The images the brush will have, each at the target size; null with <see cref="Error"/> set if something's missing.</summary>
    private Dictionary<string, PixelImage>? BuildImages()
    {
        if (Width <= 0 || Height <= 0 || Width > 4096 || Height > 4096)
        {
            Error = "Enter a size between 1 and 4096, or import an image.";
            return null;
        }
        var normalSlot = Slots.FirstOrDefault(s => s.IsNormal);
        PixelImage? Sized(PixelImage? image, FitMode mode) => image is null ? null
            : image.Width == Width && image.Height == Height ? image : ImageImport.Resize(image, Width, Height, mode);
        var normal = Sized(normalSlot?.Imported, FitMode) ?? Sized(normalSlot?.CurrentPixels, FitMode.Stretch);

        var images = new Dictionary<string, PixelImage>();
        foreach (var slot in Slots)
        {
            PixelImage? image;
            if (slot.Imported != null) { image = Sized(slot.Imported, FitMode); slot.Source = $"imported: {slot.ImportedFile}"; }
            else if (!slot.IsNormal && normal != null && (DeriveStates || slot.CurrentPixels is null))
            {
                image = ImageImport.DeriveState(normal, slot.State);
                slot.Source = "generated from Normal";
            }
            else if (slot.CurrentPixels != null) { image = Sized(slot.CurrentPixels, FitMode.Stretch); slot.Source = "unchanged"; }
            else image = null;

            if (image is null)
            {
                Error = $"Import an image for {slot.Title}.";
                return null;
            }
            slot.Result = ImageConverter.ToBitmap(image);
            images[slot.State] = image;
        }
        return images;
    }

    private async Task ReplanAsync()
    {
        _planCts?.Cancel();
        var cts = _planCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Error = null;
        _plan = null;
        Changes = [];
        Notes = [];
        AtlasPreview = null;
        PlanSummary = null;
        OnPropertyChanged(nameof(CanApply));

        string name = Name.Trim();
        if (IsNew && BrushEditor.ValidateNewName(_workspace, name) is { } nameError) { Error = nameError; return; }
        string file = IsNewFile ? "Window/" + NewBrushesFile.Trim() : BrushesFile;
        if (IsNewFile && (!file.EndsWith(".brushes", StringComparison.OrdinalIgnoreCase) || _workspace.Stack.Resolve(file) != null))
        {
            Error = "Enter a new file name ending in .brushes that doesn't exist yet.";
            return;
        }
        string? texture = TextureChoice switch
        {
            TextureChoice.Keep => CurrentTexture,
            TextureChoice.Existing => OtherTexture,
            _ => NewTexture.Trim(),
        };
        if (string.IsNullOrEmpty(texture)) { Error = "Choose a texture."; return; }
        if (TextureChoice == TextureChoice.New && BrushEditor.ValidateNewTexture(_workspace, texture) is { } textureError) { Error = textureError; return; }

        var images = BuildImages();
        if (images is null) return;
        if (TextureChoice == TextureChoice.Keep && Slots.All(slot => slot.CurrentPixels is { } now && images.TryGetValue(slot.State, out var image)
                && image.Width == now.Width && image.Height == now.Height && image.Bgra.AsSpan().SequenceEqual(now.Bgra)))
        {
            PlanSummary = "Nothing to change yet: import an image or change the size.";
            return;
        }

        IsBusy = true;
        try
        {
            // Keeping the texture of an icon sheet keeps its per-state textures too.
            var byState = TextureChoice == TextureChoice.Keep && Brush != null
                ? Brush.Images.ToDictionary(kv => kv.Key, kv => kv.Value.FileName, StringComparer.OrdinalIgnoreCase)
                : null;
            var request = new BrushEditRequest(IsNew ? name : Brush!.Name, IsNew, images, texture, TextureChoice == TextureChoice.New,
                NewTextureFormat, IsNew ? file : null, byState);
            var plan = await Task.Run(() => BrushEditor.PlanAsync(_workspace, request, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            _plan = plan;
            AtlasPreview = ImageConverter.ToBitmap(plan.Atlas);
            PlanSummary = plan.Mode switch
            {
                AtlasMode.Repack => $"Atlas repacked to {plan.AtlasWidth}×{plan.AtlasHeight}; {plan.MovedRegions} other images moved.",
                AtlasMode.Append => $"Added to the atlas's free space ({plan.AtlasWidth}×{plan.AtlasHeight}); nothing else moves.",
                _ => "Same size as before: the images are replaced where they are; nothing else changes.",
            };
            Changes = plan.Changes.Changes.Select(c => new ChangeRow(c.RelativePath, c.Description, c.IsDelete)).ToList();
            Notes = plan.Changes.Notes;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or TextureLoadException)
        {
            Error = e.Message;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanApply));
            ApplyCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (_plan is null) return;
        try
        {
            var changes = _plan.Changes;
            await Services.Busy.Current.RunInBackground("Writing the brush and its textures…", _ => { changes.Apply(_workspace); return true; });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error = $"Couldn't write the files: {e.Message}";
            return;
        }
        string name = IsNew ? Name.Trim() : Brush!.Name;
        await _reload();
        _navigate(new BrushTarget(name));
    }
}
