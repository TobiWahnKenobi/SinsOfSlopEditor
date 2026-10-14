using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SinsEditor.App.Navigation;
using SinsEditor.App.ViewModels;
using SinsEditor.Core;
using SinsEditor.Core.Index;
using System.Globalization;
using SinsEditor.Core.Meshes;
using SinsEditor.Core.Settings;
using SinsEditor.Core.Textures;
using SinsEditor.Viewer;

namespace SinsEditor.App.Assets;

public sealed record MaterialRow(int Index, string Role, string FileName, NavTarget? Target, bool Missing)
{
    public string Text => Missing ? $"{FileName} (missing)" : FileName;
}

public sealed record PointGroupRow(string Name, int Count);

/// <summary>A particle a <c>Flair-</c> hardpoint attaches, with the meshes it shows.</summary>
public sealed record MeshLink(string Name, NavTarget Target);

public sealed record AttachedParticleRow(string Particle, string Meshes, IReadOnlyList<MeshLink> MeshLinks, bool Missing)
{
    public string Text => Missing ? $"{Particle} (missing)" : Particle;
}

public sealed record UsageRow(string Title, string Detail, NavTarget Target);

/// <summary>A team colour to preview the team colour areas (diffuse alpha) with.</summary>
public sealed record TeamColorChoice(string Name, System.Numerics.Vector4 Color)
{
    public override string ToString() => Name;
}

/// <summary>What is behind the model.</summary>
public enum BackgroundMode { Skybox, Image, Brush, None }

public sealed record BackgroundChoice(BackgroundMode Mode, string Name)
{
    public override string ToString() => Name;
}

/// <summary>How far the star is: PBR shaders light ships much brighter close to a star.</summary>
public sealed record StarDistanceChoice(string Name, float Distance)
{
    public override string ToString() => Name;
}

/// <summary>A mesh in 3D with its textures and hardpoints, its materials, and the entities that use it.</summary>
public sealed partial class MeshViewModel : PageViewModel
{
    private readonly ModWorkspace _workspace;
    private readonly Action<NavTarget> _navigate;
    private readonly Func<Task> _reload;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSizeText), nameof(FactorText), nameof(CanScale))]
    private MeshData? _mesh;

    // ---- Scaling: a target size in meters at the global scale (game units per meter) ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSizeText), nameof(FactorText), nameof(CanScale))]
    private string _unitsPerMeter = "1";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSizeText), nameof(FactorText), nameof(CanScale))]
    private MeshAxis _axis = MeshAxis.Longest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FactorText), nameof(CanScale))]
    private string _targetMeters = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanScale), nameof(ReplaceInMod))]
    private bool _saveAsCopy = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanScale))]
    private string _copyName = "";

    [ObservableProperty]
    private string? _scaleError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanScale))]
    private bool _isScaling;

    [ObservableProperty]
    private IReadOnlyList<MeshTexture?>? _textures;

    [ObservableProperty]
    private bool _showPoints = true;

    [ObservableProperty]
    private bool _showWireframe;

    [ObservableProperty]
    private string? _status = "Loading…";

    [ObservableProperty]
    private string? _fileText;

    [ObservableProperty]
    private string? _statsText;

    [ObservableProperty]
    private IReadOnlyList<MaterialRow> _materials = [];

    [ObservableProperty]
    private IReadOnlyList<PointGroupRow> _points = [];

    [ObservableProperty]
    private IReadOnlyList<MeshAttachment>? _attachments;

    // ---- Game shaders: the mesh drawn with the game's own .fx, the skybox's environment maps and a team colour ----

    [ObservableProperty]
    private GameScene? _gameScene;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseSimpleView))]
    private bool _useGameShaders = true;

    [ObservableProperty]
    private string? _shader;

    [ObservableProperty]
    private string? _skybox;

    [ObservableProperty]
    private TeamColorChoice _teamColor = TeamColors[0];

    [ObservableProperty]
    private StarDistanceChoice _starDistance = StarDistances[1];

    public static IReadOnlyList<StarDistanceChoice> StarDistances { get; } =
    [
        new("Near the star (40,000)", 40_000f),
        new("Mid-system (100,000)", 100_000f),
        new("Far out (250,000)", 250_000f),
    ];

    public IReadOnlyList<StarDistanceChoice> StarDistanceChoices => StarDistances;

    partial void OnStarDistanceChanged(StarDistanceChoice value) => _ = BuildSceneAsync();

    // ---- Particles on Flair- hardpoints: simulated sprites, with playback ----

    [ObservableProperty]
    private bool _showParticles = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayText))]
    private bool _isPlaying;

    [ObservableProperty]
    private double _playbackSpeed = 1;

    /// <summary>Seconds into the particle effects (the view runs the simulations there; 3 s shows them settled).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    private double _particleTime = 3;

    [ObservableProperty]
    private double _particleDuration = 30;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasParticleEffects))]
    private int _particleEffectCount;

    public bool HasParticleEffects => ParticleEffectCount > 0;
    public string PlayText => IsPlaying ? "Pause" : "Play";
    public string TimeText => $"{ParticleTime:0.00} s";
    public IReadOnlyList<double> Speeds { get; } = [0.1, 0.25, 0.5, 1, 2, 4];

    [RelayCommand]
    private void PlayPause() => IsPlaying = !IsPlaying;

    [RelayCommand]
    private void StepBack()
    {
        IsPlaying = false;
        ParticleTime = Math.Max(0, ParticleTime - 1.0 / 30 * Math.Max(1, PlaybackSpeed * 3));
    }

    [RelayCommand]
    private void StepForward()
    {
        IsPlaying = false;
        ParticleTime = Math.Min(ParticleDuration, ParticleTime + 1.0 / 30 * Math.Max(1, PlaybackSpeed * 3));
    }

    [RelayCommand]
    private void Restart() => ParticleTime = 0;

    // ---- Background: skybox, a fixed 2D picture (file or brush), or none ----

    public static IReadOnlyList<BackgroundChoice> BackgroundChoices { get; } =
    [
        new(BackgroundMode.Skybox, "Skybox"),
        new(BackgroundMode.Image, "Image file"),
        new(BackgroundMode.Brush, "Brush"),
        new(BackgroundMode.None, "None"),
    ];

    public IReadOnlyList<BackgroundChoice> BackgroundChoiceList => BackgroundChoices;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImageBackground), nameof(IsBrushBackground))]
    private BackgroundChoice _background = BackgroundChoices[0];

    [ObservableProperty]
    private string? _backgroundText;

    [ObservableProperty]
    private string _backgroundBrush = "";

    private PixelImage? _backgroundFile, _backgroundBrushImage;

    public bool IsImageBackground => Background.Mode == BackgroundMode.Image;
    public bool IsBrushBackground => Background.Mode == BackgroundMode.Brush;
    public IReadOnlyList<string> BrushNames { get; private set; } = [];

    partial void OnBackgroundChanged(BackgroundChoice value)
    {
        if (value.Mode == BackgroundMode.Image && _backgroundFile is null) LoadBackgroundFile();
        _ = BuildSceneAsync();
    }

    partial void OnBackgroundBrushChanged(string value)
    {
        _backgroundBrushImage = BrushImage(value);
        BackgroundText = _backgroundBrushImage is { } img ? $"{img.Width} × {img.Height}" : value.Length > 0 ? "No such brush, or it has no image." : null;
        if (IsBrushBackground) _ = BuildSceneAsync();
    }

    private PixelImage? BrushImage(string name) =>
        name.Length > 0 && _workspace.Index.Brushes.Contains(name) ? _workspace.Textures.RenderBrush(name).Image : null;

    /// <summary>The fixed 2D background now chosen, if any.</summary>
    private PixelImage? BackgroundImage => Background.Mode switch
    {
        BackgroundMode.Image => _backgroundFile,
        BackgroundMode.Brush => _backgroundBrushImage,
        _ => null,
    };

    [RelayCommand]
    private void LoadBackgroundFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = Imaging.ImageFiles.DialogFilter, Title = "Background picture" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            _backgroundFile = Imaging.ImageFiles.Load(dialog.FileName);
            BackgroundText = $"{Path.GetFileName(dialog.FileName)} · {_backgroundFile.Width} × {_backgroundFile.Height}";
            if (!IsImageBackground) Background = BackgroundChoices[1];
            else _ = BuildSceneAsync();
        }
        catch (Exception e) when (e is IOException or NotSupportedException or TextureLoadException or System.Runtime.InteropServices.COMException)
        {
            BackgroundText = $"Couldn't read {Path.GetFileName(dialog.FileName)}: {e.Message}";
        }
    }

    // ---- Pictures of the view: a size, or a brush's size (optionally on its image), to a file or into the brush editor ----

    public ViewportCapture Capture { get; } = new();

    [ObservableProperty]
    private int _exportWidth = 256;

    [ObservableProperty]
    private int _exportHeight = 256;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseInBrush))]
    private string _exportBrush = "";

    [ObservableProperty]
    private bool _exportOnBrushImage;

    [ObservableProperty]
    private System.Windows.Media.Imaging.BitmapSource? _exportPreview;

    [ObservableProperty]
    private string? _exportText;

    public bool CanUseInBrush => _workspace.Index.Brushes.Contains(ExportBrush.Trim());

    partial void OnExportBrushChanged(string value)
    {
        // Take the brush's size (its Normal image).
        if (BrushImage(value.Trim()) is { } image) (ExportWidth, ExportHeight) = (image.Width, image.Height);
    }

    /// <summary>
    /// The picture to export: the view without hardpoints, at the chosen size, on the chosen 2D background (or the brush's
    /// own image), transparent if there's none.
    /// </summary>
    private PixelImage? CaptureImage()
    {
        if (ExportWidth is < 1 or > 4096 || ExportHeight is < 1 or > 4096)
        {
            ExportText = "Enter a size between 1 and 4096.";
            return null;
        }
        var background = ExportOnBrushImage && BrushImage(ExportBrush.Trim()) is { } brush ? brush : BackgroundImage;
        if (background != null && (background.Width != ExportWidth || background.Height != ExportHeight))
            background = ImageOps.Scale(background, ExportWidth, ExportHeight);
        var image = Capture.Capture(new CaptureRequest(ExportWidth, ExportHeight, background));
        if (image is null)
        {
            ExportText = "The game-shader view has nothing to capture.";
            return null;
        }
        ExportPreview = Imaging.ImageConverter.ToBitmap(image);
        ExportText = $"{image.Width} × {image.Height}" + (background is null ? ", transparent background" : "");
        return image;
    }

    [RelayCommand]
    private void PreviewExport() => CaptureImage();

    [RelayCommand]
    private void SaveImage()
    {
        if (CaptureImage() is not { } image) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PNG image (*.png)|*.png", FileName = $"{Name}_{image.Width}x{image.Height}.png" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            PngWriter.Save(image, dialog.FileName);
            ExportText = $"Saved {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ExportText = $"Couldn't save: {e.Message}";
        }
    }

    /// <summary>Opens the brush editor for the chosen brush with this picture as its new Normal image (other states derived).</summary>
    [RelayCommand]
    private void UseInBrush()
    {
        string brush = ExportBrush.Trim();
        if (!CanUseInBrush || CaptureImage() is not { } image) return;
        BrushEditing.BrushEditorViewModel.PendingImport = (image, $"view of {Name}");
        _navigate(new BrushTarget(brush));
    }

    [ObservableProperty]
    private string? _shaderNote;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasClouds))]
    private IReadOnlyList<string> _cloudPasses = [];

    [ObservableProperty]
    private bool _showClouds = true;

    [ObservableProperty]
    private bool _showSkybox = true;

    partial void OnShowSkyboxChanged(bool value) => _ = BuildSceneAsync();

    /// <summary>True once the game-shader scene is built (screenshots wait for it).</summary>
    [ObservableProperty]
    private bool _sceneReady;

    public bool HasClouds => CloudPasses.Count > 0;

    partial void OnShowCloudsChanged(bool value) => _ = BuildSceneAsync();

    private LoadedMesh? _loaded;
    private IReadOnlyList<AttachedMesh> _attached = [];
    private IReadOnlyList<AttachedEffect> _effects = [];
    private readonly Dictionary<string, PixelImage?> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CubeMap?> _cubes = new(StringComparer.OrdinalIgnoreCase);
    private int _sceneVersion;

    public static IReadOnlyList<TeamColorChoice> TeamColors { get; } =
    [
        new("Blue", new(0.16f, 0.36f, 0.86f, 1f)),
        new("Red", new(0.85f, 0.16f, 0.12f, 1f)),
        new("Green", new(0.2f, 0.7f, 0.25f, 1f)),
        new("Yellow", new(0.9f, 0.75f, 0.15f, 1f)),
        new("Purple", new(0.55f, 0.25f, 0.8f, 1f)),
        new("White", new(0.9f, 0.9f, 0.9f, 1f)),
        new("None (no team colour)", new(0f, 0f, 0f, 0f)),
    ];

    public IReadOnlyList<TeamColorChoice> TeamColorChoices => TeamColors;
    public IReadOnlyList<string> Shaders { get; private set; } = [];
    public IReadOnlyList<string> Skyboxes { get; private set; } = [];
    public bool UseSimpleView => !UseGameShaders;

    partial void OnShaderChanged(string? value) => _ = BuildSceneAsync();
    partial void OnSkyboxChanged(string? value) => _ = BuildSceneAsync();
    partial void OnTeamColorChanged(TeamColorChoice value) => _ = BuildSceneAsync();

    private PixelImage? Image(string fileName)
    {
        if (fileName.Length == 0) return null;
        lock (_images)
        {
            if (_images.TryGetValue(fileName, out var cached)) return cached;
        }
        var image = MeshLoader.LoadTexture(_workspace, fileName);
        lock (_images) _images[fileName] = image;
        return image;
    }

    private CubeMap? Cube(string textureName)
    {
        lock (_cubes)
        {
            if (_cubes.TryGetValue(textureName, out var cached)) return cached;
        }
        CubeMap? cube = null;
        if (_workspace.Index.Textures.TryGetValue(textureName, out var file))
        {
            try { cube = CubeMapLoader.Load(file.AbsolutePath); }
            catch (Exception e) when (e is IOException or TextureLoadException) { }
        }
        lock (_cubes) _cubes[textureName] = cube;
        return cube;
    }

    private IReadOnlyList<GameMaterial> GameMaterials(MeshData mesh) => mesh.Materials.Select(m => GameMaterial.From(m, Image)).ToList();

    /// <summary>
    /// Builds what the game-shader view draws: the mesh with its shader, attached particle meshes with their emitters' shaders,
    /// textures and the skybox's environment maps. Decoding happens off the UI thread.
    /// </summary>
    private async Task BuildSceneAsync()
    {
        if (_loaded is not { } loaded || Shader is not { } shader) return;
        int version = ++_sceneVersion;
        string? skybox = Skybox;
        var team = TeamColor.Color;
        float starDistance = StarDistance.Distance;
        var attached = _attached;
        bool clouds = ShowClouds;
        // The skybox backdrop mesh belonging to the environment maps: skybox02 → SkyboxBackdrop2.
        LoadedMesh? backdrop = null;
        var background = BackgroundImage;
        bool useSkybox = Background.Mode == BackgroundMode.Skybox;
        var effects = _effects;
        if (useSkybox && ShowSkybox && skybox is not null && int.TryParse(new string(skybox.Where(char.IsDigit).ToArray()), out int number))
            backdrop = await MeshLoader.LoadAsync(_workspace, "SkyboxBackdrop" + number);
        var (scene, cloudPasses) = await Task.Run(() =>
        {
            var main = GameShaders.ForDrawing(_workspace, shader);
            // Every pass, as the engine draws them (passes that don't apply discard themselves, e.g. by the material's
            // Specular alpha in a mod's planet shader); the cloud layer can be left out.
            var all = main.ShaderTechnique?.Passes.Where(p => p.HasShaders).Select(p => p.Name).ToList() ?? [];
            var cloudNames = all.Where(n => n.Contains("Cloud", StringComparison.OrdinalIgnoreCase)).ToList();
            HashSet<string>? chosen = clouds || cloudNames.Count == 0 ? null : [.. all.Except(cloudNames)];

            var items = new List<GameSceneItem>
            {
                new(loaded.Data, System.Numerics.Matrix4x4.Identity, GameMaterials(loaded.Data), main, chosen, PlanetTextures(), PlanetColors()),
            };
            foreach (var a in attached)
            {
                var effect = GameShaders.ForDrawing(_workspace, a.Effect.Length > 0 ? a.Effect : GameShaders.Ship);
                // Mesh particles that turn (a rotating hull section) move with the playback time.
                items.Add(new GameSceneItem(a.Mesh.Data, a.Placement, GameMaterials(a.Mesh.Data), effect,
                    PlacementAt: a.Emitter is { AngularSpeed: not 0 } ? a.PlacementAt : null));
            }
            // Compile here, off the UI thread: the renderer then finds them compiled.
            foreach (var effect in items.Select(i => i.Effect).DistinctBy(e => e.Name))
            {
                try { GameShaderCompiler.Compile(effect); }
                catch (ShaderCompileException) { }
            }
            var environment = skybox is null ? null : Cube(skybox + "environmentcube");
            var irradiance = skybox is null ? null : Cube(skybox + "irradiance");
            GameSceneItem? sky = null;
            if (backdrop is not null)
            {
                var skyEffect = GameShaders.ForDrawing(_workspace, "GS_Skybox.fx");
                try { GameShaderCompiler.Compile(skyEffect); }
                catch (ShaderCompileException) { }
                sky = new GameSceneItem(backdrop.Data, System.Numerics.Matrix4x4.Identity, GameMaterials(backdrop.Data), skyEffect);
            }
            var particles = effects.Select((a, index) => new GameParticleEffect(a.Placement, new ParticleSystem(a.Definition, seed: index + 1),
                    a.Definition.Emitters.Select(emitter => emitter.Text("MeshName").Length > 0 ? null : new GameEmitter(
                        GameShaders.ForDrawing(_workspace, emitter.Text("PipelineEffectID", "Particle_Additive.fx")),
                        emitter.List("textureName") is { Count: > 0 } t && t[0].Length > 0 ? Image(t[0]) : null,
                        emitter.List("textureName") is { Count: > 1 } t2 && t2[1].Length > 0 ? Image(t2[1]) : null)).ToList()))
                .ToList();
            foreach (var effect in particles.SelectMany(p => p.Emitters).OfType<GameEmitter>().Select(e => e.Effect).DistinctBy(e => e.Name))
            {
                try { GameShaderCompiler.Compile(effect); }
                catch (ShaderCompileException) { }
            }
            return (new GameScene(loaded.Data, items, environment, irradiance, team, sky, starDistance, particles, background), cloudNames);
        });
        if (version != _sceneVersion) return;
        if (!CloudPasses.SequenceEqual(cloudPasses)) CloudPasses = cloudPasses;
        var notes = scene.Items.Select(i => i.Effect.Name).Distinct().ToList();
        ShaderNote = $"Drawn with {string.Join(", ", notes)}" + (_workspace.Stack.Resolve("PipelineEffect/" + shader) is { Layer.Kind: not Core.FileSystem.LayerKind.BaseGame } f
            ? $" (from {f.Layer.Name})" : "") + ".";
        GameScene = scene;
        SceneReady = true;
    }

    /// <summary>A planet's cloud and glow colours (g_CloudColor from its meshInfo with this mesh, g_GlowColor), as the engine passes them.</summary>
    private Dictionary<string, System.Numerics.Vector4>? PlanetColors()
    {
        var reference = _workspace.Index.IncomingReferences(ReferenceKind.Mesh, Name).FirstOrDefault(r => r.Source.EntityType == "Planet");
        if (reference is null) return null;
        var lines = reference.Source.Document.Lines;
        static uint Hex(string v) => uint.TryParse(v, System.Globalization.NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint x) ? x : 0xFFFFFFFF;
        var colors = new Dictionary<string, System.Numerics.Vector4>();
        // cloudColor follows the meshName in the same meshInfo block.
        for (int i = reference.LineIndex + 1; i < lines.Count && i < reference.LineIndex + 6; i++)
            if (lines[i].Key == "cloudColor") { colors["g_CloudColor"] = MeshMaterial.ToVector(Hex(lines[i].Value)); break; }
        if (lines.FirstOrDefault(l => l.Key == "glowColor") is { } glow) colors["g_GlowColor"] = MeshMaterial.ToVector(Hex(glow.Value));
        return colors;
    }

    /// <summary>A planet's cloud layer (g_TextureDiffuse2), from the first Planet entity that uses the mesh.</summary>
    private Dictionary<string, PixelImage>? PlanetTextures()
    {
        var planet = _workspace.Index.IncomingReferences(ReferenceKind.Mesh, Name)
            .Select(r => r.Source).FirstOrDefault(e => e.EntityType == "Planet");
        if (planet?.Document.Lines.FirstOrDefault(l => l.Key == "cloudLayerTextureName") is not { } line
            || !Core.Documents.SinsValue.TryGetString(line.Value, out var cloud) || Image(cloud) is not { } image)
            return null;
        return new Dictionary<string, PixelImage> { ["g_TextureDiffuse2"] = image };
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttachedParticles), nameof(ScaleAlsoText))]
    private IReadOnlyList<AttachedParticleRow> _attachedParticles = [];

    public MeshViewModel(ModWorkspace workspace, string name, Action<NavTarget> navigate, Func<Task> reload)
    {
        _workspace = workspace;
        _navigate = navigate;
        _reload = reload;
        Name = Path.GetFileNameWithoutExtension(name);
        _copyName = Name + "_Scaled";
        _unitsPerMeter = EditorSettings.Load().MeshUnitsPerMeter.ToString(CultureInfo.CurrentCulture);
        Shaders = GameShaders.All(workspace);
        BrushNames = workspace.Index.Brushes.Entries.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();
        Skyboxes = GameShaders.Skyboxes(workspace);
        _skybox = Skyboxes.FirstOrDefault(s => s.Equals("skybox02", StringComparison.OrdinalIgnoreCase)) ?? Skyboxes.FirstOrDefault();
        _shader = GameShaders.ForMesh(workspace, Name);
        UsedBy = workspace.Index.IncomingReferences(ReferenceKind.Mesh, Name)
            .Concat(workspace.Index.IncomingReferences(ReferenceKind.Mesh, Name + ".mesh"))
            .Select(r => new UsageRow(workspace.Index.DisplayName(r.Source) ?? r.Source.Name, $"{r.Source.Name} · {r.Key}", new EntityTarget(r.Source.Name, r.LineIndex)))
            .DistinctBy(r => r.Detail)
            .ToList();
        _ = LoadAsync();
    }

    public string Name { get; }
    public override string Title => Name;
    public override string? Breadcrumb => "Meshes";
    public IReadOnlyList<UsageRow> UsedBy { get; }
    public bool HasNoUsedBy => UsedBy.Count == 0;
    public IReadOnlyList<MeshAxis> Axes { get; } = Enum.GetValues<MeshAxis>();

    private static double? Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double v) && v > 0 ? v
        : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0 ? v : null;

    public string? CurrentSizeText => Mesh is null ? null
        : $"Now {MeshScaler.Size(Mesh, Axis):0.##} units" + (Number(UnitsPerMeter) is { } g ? $" = {MeshScaler.Size(Mesh, Axis) / g:0.##} m" : "");

    private float? Factor => Mesh is not null && Number(UnitsPerMeter) is { } g && Number(TargetMeters) is { } t ? MeshScaler.FactorFor(Mesh, Axis, t, g) : null;

    public string? FactorText => Factor is { } f ? $"Scale factor {f:0.####} ({MeshScaler.Size(Mesh!, Axis) * f:0.##} units)" : null;

    public bool HasAttachedParticles => AttachedParticles.Count > 0;

    /// <summary>What scaling takes along: attached particles and their meshes, copied with the same suffix for a copy.</summary>
    public string? ScaleAlsoText
    {
        get
        {
            var particles = AttachedParticles.Where(p => !p.Missing).ToList();
            if (particles.Count == 0) return null;
            var meshes = particles.SelectMany(p => p.MeshLinks.Select(m => m.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string what = $"{string.Join(", ", particles.Select(p => p.Particle + ".particle"))}" + (meshes.Count > 0 ? $" and {string.Join(", ", meshes)}" : "");
            return SaveAsCopy
                ? $"Also copied and scaled with suffix {MeshScaler.CopySuffix(Name, CopyName.Trim())}: {what}."
                : $"Also scaled in the mod: {what}. Other meshes using them change too.";
        }
    }

    partial void OnSaveAsCopyChanged(bool value) => OnPropertyChanged(nameof(ScaleAlsoText));
    partial void OnCopyNameChanged(string value) => OnPropertyChanged(nameof(ScaleAlsoText));

    public bool ReplaceInMod
    {
        get => !SaveAsCopy;
        set => SaveAsCopy = !value;
    }

    public bool CanScale => Factor is not null && !IsScaling && (!SaveAsCopy || CopyName.Trim().Length > 0);

    partial void OnUnitsPerMeterChanged(string value)
    {
        if (Number(value) is not { } g) return;
        // The global scale is an editor setting, shared by all meshes and mods.
        var settings = EditorSettings.Load();
        settings.MeshUnitsPerMeter = g;
        settings.Save();
    }

    [RelayCommand]
    private async Task ScaleAsync()
    {
        if (Factor is not { } factor) return;
        string target = SaveAsCopy ? CopyName.Trim() : Name;
        if (MeshScaler.ValidateName(_workspace, target, SaveAsCopy) is { } error) { ScaleError = error; return; }
        ScaleError = null;
        IsScaling = true;
        try
        {
            await Services.Busy.Current.RunAsync($"Scaling {Name} by {factor:0.###}…", async _ => { await MeshScaler.ScaleAsync(_workspace, Name, factor, target); });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Core.Conversion.ConvertDataException or InvalidOperationException)
        {
            ScaleError = $"Couldn't scale the mesh: {e.Message}";
            return;
        }
        finally
        {
            IsScaling = false;
        }
        await _reload();
        _navigate(new MeshTarget(target));
    }

    private async Task LoadAsync()
    {
        try
        {
            var loaded = await MeshLoader.LoadAsync(_workspace, Name);
            if (loaded is null)
            {
                Status = $"No layer has Mesh/{Name}.mesh.";
                return;
            }
            var data = loaded.Data;
            FileText = $"{loaded.File.RelativePath} · {loaded.File.Layer.Name}";
            StatsText = $"{data.Vertices.Count:N0} vertices · {data.Triangles.Count:N0} triangles · radius {data.BoundingRadius:0.#} · " +
                        $"{data.MaxExtents.X - data.MinExtents.X:0.#} × {data.MaxExtents.Y - data.MinExtents.Y:0.#} × {data.MaxExtents.Z - data.MinExtents.Z:0.#}";
            Materials = data.Materials.SelectMany((m, i) => new[]
                {
                    ("Diffuse", m.Diffuse), ("Self-illumination", m.SelfIllumination), ("Normal map", m.Normal), ("Displacement", m.Displacement), ("Team color", m.TeamColor),
                }
                .Where(t => t.Item2.Length > 0)
                .Select(t =>
                {
                    bool exists = MeshLoader.ResolveTexture(_workspace, t.Item2) != null;
                    return new MaterialRow(i, t.Item1, t.Item2, exists ? new TextureTarget(Path.GetFileNameWithoutExtension(t.Item2)) : null, !exists);
                }))
                .ToList();
            Points = data.Points.GroupBy(p => p.Name).OrderBy(g => g.Key).Select(g => new PointGroupRow(g.Key, g.Count())).ToList();

            // Textures as PNG for the viewport; decoding DDS/TGA happens off the UI thread. The alpha of a ship's
            // diffuse (-cl) texture is a gloss mask, not transparency, so it's dropped.
            var textures = await Task.Run(() => data.Materials
                .Select(m => MeshLoader.LoadTexture(_workspace, m.Diffuse) is { } image ? new MeshTexture(PngWriter.Encode(Opaque(image))) : null)
                .ToList());
            Textures = textures;

            // Particles on Flair- hardpoints, and the meshes they show, placed on the main mesh.
            var particles = await MeshLoader.AttachedParticlesAsync(_workspace, data);
            AttachedParticles = particles.Select(p => new AttachedParticleRow(p.Name,
                string.Join(", ", p.MeshFileNames),
                p.MeshFileNames.Select(m => new MeshLink(m, new MeshTarget(Path.GetFileNameWithoutExtension(m)))).ToList(),
                p.File is null)).ToList();
            var attached = await MeshLoader.AttachedMeshesAsync(_workspace, data);
            _effects = await MeshLoader.AttachedEffectsAsync(_workspace, data);
            ParticleEffectCount = _effects.Count;
            // At least 30 seconds; longer if an effect with a fixed lifetime lasts longer.
            ParticleDuration = Math.Max(30, _effects.Select(e => e.Definition.HasInfiniteLifeTime ? 0f : e.Definition.TotalLifeTime).DefaultIfEmpty(0f).Max());
            var textureCache = new Dictionary<string, IReadOnlyList<MeshTexture?>>(StringComparer.OrdinalIgnoreCase);
            var attachments = new List<MeshAttachment>();
            foreach (var a in attached)
            {
                if (!textureCache.TryGetValue(a.Mesh.Name, out var meshTextures))
                    textureCache[a.Mesh.Name] = meshTextures = await Task.Run(() => a.Mesh.Data.Materials
                        .Select(m => MeshLoader.LoadTexture(_workspace, m.Diffuse) is { } image ? new MeshTexture(PngWriter.Encode(Opaque(image))) : null)
                        .ToList());
                attachments.Add(new MeshAttachment(a.Mesh.Data, a.Placement, meshTextures));
            }
            Attachments = attachments;
            _loaded = loaded;
            _attached = attached;
            Mesh = data;
            Status = null;
            await BuildSceneAsync();
        }
        catch (Exception e) when (e is IOException or FormatException or Core.Conversion.ConvertDataException)
        {
            Status = $"Couldn't read the mesh: {e.Message}";
        }
    }

    private static PixelImage Opaque(PixelImage image)
    {
        var bgra = (byte[])image.Bgra.Clone();
        for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
        return new PixelImage(image.Width, image.Height, bgra);
    }

    [RelayCommand]
    private void Open(NavTarget? target)
    {
        if (target != null) _navigate(target);
    }
}
