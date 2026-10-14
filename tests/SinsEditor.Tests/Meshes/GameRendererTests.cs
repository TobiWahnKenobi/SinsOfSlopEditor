using SinsEditor.Core;
using SinsEditor.Core.Meshes;
using SinsEditor.Core.Textures;
using SinsEditor.Viewer;
using Xunit.Abstractions;

namespace SinsEditor.Tests.Meshes;

/// <summary>Renders meshes offscreen with game shaders and checks that something is drawn.</summary>
public sealed class GameRendererTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly string _mod = Path.Combine(Path.GetTempPath(), "SinsEditorTests", "RenderMod-" + Guid.NewGuid().ToString("N"));
    private ModWorkspace? _workspace;

    public async Task InitializeAsync()
    {
        if (TestPaths.GameDir is not { } game) return;
        Directory.CreateDirectory(_mod);
        File.Copy(Path.Combine(game, "entity.manifest"), Path.Combine(_mod, "entity.manifest"));
        _workspace = await ModWorkspace.OpenAsync(new WorkspaceOptions(game, _mod, [], "English"),
            cacheRoot: Path.Combine(Path.GetTempPath(), "SinsEditorTests", "cache"));
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_mod)) Directory.Delete(_mod, recursive: true);
        return Task.CompletedTask;
    }

    private static PixelImage? Load(ModWorkspace ws, string name) => MeshLoader.LoadTexture(ws, name);

    private async Task<(int Drawn, string? Error)> RenderAsync(string meshName, EffectFile effect, string png, IReadOnlyCollection<string>? passes = null)
    {
        var ws = _workspace!;
        var mesh = (await MeshLoader.LoadAsync(ws, meshName))!.Data;
        var materials = mesh.Materials.Select(m => GameMaterial.From(m, n => Load(ws, n))).ToList();
        string? sky = GameShaders.Skyboxes(ws).FirstOrDefault();
        CubeMap? Cube(string n) => ws.Index.Textures.TryGetValue(n, out var f) ? CubeMapLoader.Load(f.AbsolutePath) : null;
        var scene = new GameScene(mesh, [new GameSceneItem(mesh, System.Numerics.Matrix4x4.Identity, materials, effect, passes)],
            sky is null ? null : Cube(sky + "environmentcube"), sky is null ? null : Cube(sky + "irradiance"), new(0.2f, 0.4f, 0.9f, 1));
        using var renderer = new GameRenderer();
        renderer.SetScene(scene);
        var pixels = renderer.Render(400, 300, OrbitCamera.Frame(mesh), new GameRenderOptions(false, false));
        int drawn = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            if (Math.Abs(pixels[i] - 34) + Math.Abs(pixels[i + 1] - 30) + Math.Abs(pixels[i + 2] - 30) > 12) drawn++;
        var bgra = new byte[pixels.Length];
        System.Buffer.BlockCopy(pixels, 0, bgra, 0, pixels.Length);
        for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
        if (Environment.GetEnvironmentVariable("SINSEDITOR_REPORT") is { } dir) PngWriter.Save(new PixelImage(400, 300, bgra), Path.Combine(Path.GetDirectoryName(dir)!, png));
        output.WriteLine($"{meshName} with {effect.Name} {(passes is null ? "" : string.Join("+", passes))}: {drawn} pixels drawn; error: {renderer.Error}");
        foreach (var m in mesh.Materials) output.WriteLine($"  material {m.Diffuse} {m.SelfIllumination} {m.Normal} {m.TeamColor} spec {m.SpecularColor:x8} diff {m.DiffuseColor:x8}");
        return (drawn, renderer.Error);
    }

    [SkippableTheory]
    [InlineData("Frigate_PhaseAntiFighter", "GS_ShadowedShip.fx")]
    [InlineData("AsteroidBig00", "GS_ShadowedAsteroid.fx")]
    [InlineData("Planet_Terran_2", "GS_Planet.fx")]
    [InlineData("SkyboxBackdrop2", "GS_Skybox.fx")]
    public async Task Base_game_meshes_are_drawn_with_base_game_shaders(string mesh, string fx)
    {
        Skip.If(_workspace is null, "Game directory not configured.");
        var (drawn, error) = await RenderAsync(mesh, GameShaders.Load(_workspace!, fx)!, $"render_{mesh}_{fx}.png");
        Assert.Null(error);
        Assert.True(drawn > 400 * 300 / 20, $"only {drawn} pixels drawn");
    }

    [SkippableFact]
    public async Task Mod_planet_shader_draws_the_planet()
    {
        Skip.If(_workspace is null || TestPaths.TestMod is null, "Game directory or test mod not configured.");
        string fx = Path.Combine(TestPaths.TestMod!, "PipelineEffect", "GS_Planet.fx");
        Skip.IfNot(File.Exists(fx), "Test mod has no GS_Planet.fx.");
        var effect = EffectFile.Parse("GS_Planet.fx", File.ReadAllText(fx));
        foreach (var pass in effect.ShaderTechnique!.Passes) await RenderAsync("Planet_Terran_2", effect, $"render_planet_mod_{pass.Name}.png", [pass.Name]);
        var (drawn, error) = await RenderAsync("Planet_Terran_2", effect, "render_planet_mod.png");
        Assert.Null(error);
        Assert.True(drawn > 400 * 300 / 20, $"only {drawn} pixels drawn");
    }

    [SkippableFact]
    public async Task Skybox_of_a_mod_renders_around_the_ship()
    {
        string ai = Path.Combine(Path.GetDirectoryName(TestPaths.TestMod ?? "")!, "SinsRemasteredAI");
        Skip.If(TestPaths.GameDir is null || !Directory.Exists(ai), "SinsRemasteredAI not found.");
        var ws = await ModWorkspace.OpenAsync(new WorkspaceOptions(TestPaths.GameDir!, _mod, [ai], "English"),
            cacheRoot: Path.Combine(Path.GetTempPath(), "SinsEditorTests", "cache"));
        var mesh = (await MeshLoader.LoadAsync(ws, "Frigate_PhaseAntiFighter"))!.Data;
        var sky = (await MeshLoader.LoadAsync(ws, "SkyboxBackdrop2"))!;
        output.WriteLine($"skybox mesh from {sky.File.Layer.Name}; shader from {ws.Stack.Resolve("PipelineEffect/GS_Skybox.fx")!.Layer.Name}");
        foreach (var m in sky.Data.Materials) output.WriteLine($"  {m.Diffuse} {m.SelfIllumination} diff {m.DiffuseColor:x8} amb {m.AmbientColor:x8} spec {m.SpecularColor:x8} gloss {m.Glossiness}");
        GameMaterial Mat(MeshMaterial m) => GameMaterial.From(m, n => MeshLoader.LoadTexture(ws, n));
        var scene = new GameScene(mesh, [new GameSceneItem(mesh, System.Numerics.Matrix4x4.Identity, mesh.Materials.Select(Mat).ToList(), GameShaders.ForDrawing(ws, "GS_ShadowedShip.fx"))],
            null, null, new(0.2f, 0.4f, 0.9f, 1), new GameSceneItem(sky.Data, System.Numerics.Matrix4x4.Identity, sky.Data.Materials.Select(Mat).ToList(), GameShaders.ForDrawing(ws, "GS_Skybox.fx")));
        using var renderer = new GameRenderer();
        renderer.SetScene(scene);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var pixels = renderer.Render(400, 300, OrbitCamera.Frame(mesh), new GameRenderOptions(false, false));
        int lit = 0;
        for (int i = 0; i < pixels.Length; i += 4) if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 30) lit++;
        output.WriteLine($"rendered in {watch.ElapsedMilliseconds} ms, error {renderer.Error}, {lit} lit pixels");
        // The backdrop fills the view (the parallax nebula used to hang the GPU, and its pixel shader read shifted inputs).
        Assert.Null(renderer.Error);
        Assert.True(lit > 400 * 300 / 2, $"only {lit} lit pixels");
        if (Environment.GetEnvironmentVariable("SINSEDITOR_REPORT") is { } dir)
        {
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            PngWriter.Save(new PixelImage(400, 300, pixels), Path.Combine(Path.GetDirectoryName(dir)!, "render_skybox.png"));
        }
    }

    [SkippableFact]
    public async Task Skybox_changes_the_ships_reflections()
    {
        string ai = Path.Combine(Path.GetDirectoryName(TestPaths.TestMod ?? "")!, "SinsRemasteredAI");
        Skip.If(TestPaths.GameDir is null || !Directory.Exists(ai), "SinsRemasteredAI not found.");
        var ws = await ModWorkspace.OpenAsync(new WorkspaceOptions(TestPaths.GameDir!, _mod, [ai], "English"),
            cacheRoot: Path.Combine(Path.GetTempPath(), "SinsEditorTests", "cache"));
        var mesh = (await MeshLoader.LoadAsync(ws, "Frigate_PhaseAntiFighter"))!.Data;
        var materials = mesh.Materials.Select(m => GameMaterial.From(m, n => MeshLoader.LoadTexture(ws, n))).ToList();
        var effect = GameShaders.ForDrawing(ws, "GS_ShadowedShip.fx");
        output.WriteLine($"skyboxes: {string.Join(", ", GameShaders.Skyboxes(ws))}");
        CubeMap? Cube(string n)
        {
            if (!ws.Index.Textures.TryGetValue(n, out var f)) { output.WriteLine($"{n}: not found"); return null; }
            var c = CubeMapLoader.Load(f.AbsolutePath);
            long sum = 0; foreach (var face in c.Faces) foreach (var b in face.Bgra) sum += b;
            output.WriteLine($"{n}: {f.Layer.Name} {c.Size}px, mean {sum / (6.0 * c.Faces[0].Bgra.Length):0.0}");
            return c;
        }
        byte[] Render(string sky)
        {
            var scene = new GameScene(mesh, [new GameSceneItem(mesh, System.Numerics.Matrix4x4.Identity, materials, effect)],
                Cube(sky + "environmentcube"), Cube(sky + "irradiance"), new(0.2f, 0.4f, 0.9f, 1));
            using var renderer = new GameRenderer();
            renderer.SetScene(scene);
            return renderer.Render(400, 300, OrbitCamera.Frame(mesh), new GameRenderOptions(false, false));
        }
        var a = Render("skybox02");
        var b = Render("skybox06");
        if (Environment.GetEnvironmentVariable("SINSEDITOR_REPORT") is { } dir)
            foreach (var (name, px) in new[] { ("02", a), ("06", b) })
            {
                var copy = (byte[])px.Clone();
                for (int i = 3; i < copy.Length; i += 4) copy[i] = 255;
                PngWriter.Save(new PixelImage(400, 300, copy), Path.Combine(Path.GetDirectoryName(dir)!, $"render_sky{name}.png"));
            }
        long diff = 0;
        for (int i = 0; i < a.Length; i++) diff += Math.Abs(a[i] - b[i]);
        output.WriteLine($"mean pixel difference between skyboxes: {diff / (double)a.Length:0.00}");
        // The ship covers about a tenth of the picture; with the light inside the star's radius everything saturated (0.03).
        Assert.True(diff / (double)a.Length > 0.15, "the skybox has no effect on the ship");
    }
}
