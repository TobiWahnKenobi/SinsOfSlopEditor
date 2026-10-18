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

    [SkippableFact]
    public async Task Flair_particles_simulate_and_draw()
    {
        Skip.If(_workspace is null, "Game directory not configured.");
        var ws = _workspace!;
        var mesh = (await MeshLoader.LoadAsync(ws, "StarBasePsi-Culture"))!.Data;
        var effects = await MeshLoader.AttachedEffectsAsync(ws, mesh);
        Assert.NotEmpty(effects);
        foreach (var effect in effects.DistinctBy(e => e.Particle))
        {
            var system = new ParticleSystem(effect.Definition);
            var counts = new List<int>();
            for (int t = 1; t <= 8; t++) { system.RunTo(t * 0.5f); counts.Add(system.Particles.Count); }
            output.WriteLine($"{effect.Particle}: {effect.Definition.Emitters.Count} emitters ({string.Join(", ", effect.Definition.Emitters.Select(e => $"{e.Type} {e.Text("PipelineEffectID")} rate {e.Float("EmitRate")} life {e.Float("ParticleLifeTime")} size {e.Float("ParticleWidth")}"))}); particles at 0.5 s steps: {string.Join(" ", counts)}");
        }

        GameMaterial Mat(MeshMaterial m) => GameMaterial.From(m, n => MeshLoader.LoadTexture(ws, n));
        var particles = effects.Select((a, index) => new GameParticleEffect(a.Placement, new ParticleSystem(a.Definition, seed: index + 1),
            a.Definition.Emitters.Select(e => e.Text("MeshName").Length > 0 ? null : new GameEmitter(
                GameShaders.ForDrawing(ws, e.Text("PipelineEffectID", "Particle_Additive.fx")),
                e.List("textureName") is { Count: > 0 } t && t[0].Length > 0 ? MeshLoader.LoadTexture(ws, t[0]) : null, null)).ToList())).ToList();
        foreach (var p in particles) p.System.RunTo(3.5f);
        var scene = new GameScene(mesh, [], null, null, new(0.2f, 0.4f, 0.9f, 1), Particles: particles);
        using var renderer = new GameRenderer();
        renderer.SetScene(scene);
        var pixels = renderer.Render(400, 300, OrbitCamera.Frame(mesh), new GameRenderOptions(false, false));
        int lit = 0;
        for (int i = 0; i < pixels.Length; i += 4) if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 120) lit++;
        output.WriteLine($"particles alive {particles.Sum(p => p.System.Particles.Count)}, lit pixels {lit}, error {renderer.Error}");
        if (Environment.GetEnvironmentVariable("SINSEDITOR_REPORT") is { } dir)
        {
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            PngWriter.Save(new PixelImage(400, 300, pixels), Path.Combine(Path.GetDirectoryName(dir)!, "render_particles.png"));
        }
        Assert.Null(renderer.Error);
        Assert.True(lit > 50, $"only {lit} lit pixels");
    }

    [SkippableFact]
    public async Task Transparent_pictures_have_the_mesh_opaque_and_the_rest_clear()
    {
        Skip.If(_workspace is null, "Game directory not configured.");
        var ws = _workspace!;
        var mesh = (await MeshLoader.LoadAsync(ws, "Frigate_PhaseAntiFighter"))!.Data;
        var scene = new GameScene(mesh, [new GameSceneItem(mesh, System.Numerics.Matrix4x4.Identity,
            mesh.Materials.Select(m => GameMaterial.From(m, n => MeshLoader.LoadTexture(ws, n))).ToList(), GameShaders.ForDrawing(ws, "GS_ShadowedShip.fx"))],
            null, null, new(0.2f, 0.4f, 0.9f, 1));
        using var renderer = new GameRenderer();
        renderer.SetScene(scene);
        var pixels = renderer.Render(200, 150, OrbitCamera.Frame(mesh), new GameRenderOptions(true, false, Transparent: true));
        Assert.Equal(0, pixels[3]);                                     // corner
        Assert.Equal(255, pixels[(75 * 200 + 100) * 4 + 3]);           // centre of the ship
        int opaque = 0;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] == 255) opaque++;
        Assert.InRange(opaque, 200 * 150 / 20, 200 * 150 / 2);

        // A background makes it opaque everywhere.
        var background = new PixelImage(4, 4, Enumerable.Repeat((byte)200, 64).ToArray());
        var withBackground = renderer.Render(200, 150, OrbitCamera.Frame(mesh), new GameRenderOptions(false, false, Background: background));
        Assert.Equal(200, withBackground[0]);
    }

    [SkippableFact]
    public async Task Cel_shading_draws_flat_bands_with_outlines()
    {
        Skip.If(_workspace is null, "Game directory not configured.");
        var ws = _workspace!;
        var mesh = (await MeshLoader.LoadAsync(ws, "Frigate_PhaseAntiFighter"))!.Data;
        var scene = new GameScene(mesh, [new GameSceneItem(mesh, System.Numerics.Matrix4x4.Identity,
            mesh.Materials.Select(m => GameMaterial.From(m, n => MeshLoader.LoadTexture(ws, n))).ToList(), GameShaders.ForDrawing(ws, "GS_ShadowedShip.fx"))],
            null, null, new(0.2f, 0.4f, 0.9f, 1));
        using var renderer = new GameRenderer();
        renderer.SetScene(scene);
        // Mono fill in red, two bands, green lines: only those colours (and their mixes at the edges) appear on the mesh.
        var style = Core.Settings.CelStyle.Default with { UseTexture = false, FillColor = "#FF0000", Bands = 2, Shadow = 0.5, LineColor = "#00FF00", LineWidth = 3 };
        var pixels = renderer.Render(200, 150, OrbitCamera.Frame(mesh), new GameRenderOptions(false, false, Transparent: true, LightDirection: new(1, 0.3f, 0), Cel: style));
        Assert.Null(renderer.Error);
        int lines = 0, full = 0, half = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] < 255) continue;
            byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            if (g > 200 && r < 40) lines++;
            else if (r > 240 && g < 20) full++;
            else if (r is > 110 and < 145 && g < 20) half++;
        }
        output.WriteLine($"lines {lines}, lit {full}, shadow {half}");
        if (Environment.GetEnvironmentVariable("SINSEDITOR_REPORT") is { } dir) PngWriter.Save(new PixelImage(200, 150, pixels), Path.Combine(Path.GetDirectoryName(dir)!, "cel.png"));
        Assert.True(lines > 100, "outline drawn");
        Assert.True(full > 10 && half > 10 && full + half > 1000, "both bands drawn");
        // The corner stays clear; the outline makes pixels just outside the mesh opaque too.
        Assert.Equal(0, pixels[3]);
    }
}
