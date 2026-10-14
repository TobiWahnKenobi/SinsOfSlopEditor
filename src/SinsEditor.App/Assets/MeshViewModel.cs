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
    private string? _shaderNote;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasClouds))]
    private IReadOnlyList<string> _cloudPasses = [];

    [ObservableProperty]
    private bool _showClouds = true;

    /// <summary>True once the game-shader scene is built (screenshots wait for it).</summary>
    [ObservableProperty]
    private bool _sceneReady;

    public bool HasClouds => CloudPasses.Count > 0;

    partial void OnShowCloudsChanged(bool value) => _ = BuildSceneAsync();

    private LoadedMesh? _loaded;
    private IReadOnlyList<AttachedMesh> _attached = [];
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
        var attached = _attached;
        bool clouds = ShowClouds;
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
                items.Add(new GameSceneItem(a.Mesh.Data, a.Placement, GameMaterials(a.Mesh.Data), effect));
            }
            // Compile here, off the UI thread: the renderer then finds them compiled.
            foreach (var effect in items.Select(i => i.Effect).DistinctBy(e => e.Name))
            {
                try { GameShaderCompiler.Compile(effect); }
                catch (ShaderCompileException) { }
            }
            var environment = skybox is null ? null : Cube(skybox + "environmentcube");
            var irradiance = skybox is null ? null : Cube(skybox + "irradiance");
            return (new GameScene(loaded.Data, items, environment, irradiance, team), cloudNames);
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
                    ("Diffuse", m.Diffuse), ("Self-illumination", m.SelfIllumination), ("Normal map", m.Displacement), ("Team color", m.TeamColor),
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
