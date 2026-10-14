using System.Numerics;
using SinsEditor.Core;
using SinsEditor.Core.Documents;
using SinsEditor.Core.Meshes;

namespace SinsEditor.Tests.Meshes;

public sealed class MeshTests : IAsyncLifetime
{
    private readonly string _mod = Path.Combine(Path.GetTempPath(), "SinsEditorTests", "MeshMod-" + Guid.NewGuid().ToString("N"));
    private ModWorkspace? _workspace;

    public async Task InitializeAsync()
    {
        if (TestPaths.GameDir is not { } game) return;
        Directory.CreateDirectory(_mod);
        File.Copy(Path.Combine(game, "entity.manifest"), Path.Combine(_mod, "entity.manifest"));
        _workspace = await Open();
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_mod)) Directory.Delete(_mod, recursive: true);
        return Task.CompletedTask;
    }

    private Task<ModWorkspace> Open() => ModWorkspace.OpenAsync(new WorkspaceOptions(TestPaths.GameDir!, _mod, [], "English"),
        cacheRoot: Path.Combine(Path.GetTempPath(), "SinsEditorTests", "cache"));

    private ModWorkspace Workspace
    {
        get
        {
            Skip.If(_workspace is null, "Game directory not configured.");
            return _workspace!;
        }
    }

    [SkippableFact]
    public async Task Reads_game_meshes_completely()
    {
        var ws = Workspace;
        foreach (string name in ws.Index.Meshes.Keys.Where(n => n.StartsWith("Frigate", StringComparison.OrdinalIgnoreCase)).Take(8))
        {
            var mesh = (await MeshLoader.LoadAsync(ws, name))!;
            var doc = SinsDocument.Load(await ws.Cache.GetTextPathAsync(mesh.File.AbsolutePath));
            int Declared(string key) => int.Parse(doc.Lines.First(l => l.Key == key).Value);
            Assert.Equal(Declared("NumVertices"), mesh.Data.Vertices.Count);
            Assert.Equal(Declared("NumTriangles"), mesh.Data.Triangles.Count);
            Assert.Equal(Declared("NumPoints"), mesh.Data.Points.Count);
            Assert.Equal(Declared("NumMaterials"), mesh.Data.Materials.Count);
            Assert.All(mesh.Data.Triangles, t => Assert.True(t.A < mesh.Data.Vertices.Count && t.B < mesh.Data.Vertices.Count && t.C < mesh.Data.Vertices.Count));
            Assert.True(mesh.Data.BoundingRadius > 0);
        }
    }

    [SkippableFact]
    public async Task Scaling_writes_a_binary_copy_with_scaled_geometry()
    {
        var ws = Workspace;
        string name = ws.Index.Meshes.Keys.First(n => n.StartsWith("Frigate", StringComparison.OrdinalIgnoreCase));
        var original = (await MeshLoader.LoadAsync(ws, name))!.Data;
        Assert.Null(MeshScaler.ValidateName(ws, name + "_Big", isCopy: true));
        Assert.NotNull(MeshScaler.ValidateName(ws, name, isCopy: true));

        string path = (await MeshScaler.ScaleAsync(ws, name, 1.5f, name + "_Big"))[0];
        Assert.Equal(SinsFileFormat.Binary, SinsFileFormatDetector.Detect(Path.Combine(_mod, path)));

        var after = await Open();
        var scaled = (await MeshLoader.LoadAsync(after, name + "_Big"))!.Data;
        Assert.Equal(original.Vertices.Count, scaled.Vertices.Count);
        Assert.Equal(original.BoundingRadius * 1.5f, scaled.BoundingRadius, 0.01f);
        Assert.True(Vector3.Distance(original.MaxExtents * 1.5f, scaled.MaxExtents) < 0.01f, $"extents {original.MaxExtents} {original.MinExtents} r {original.BoundingRadius} vs {scaled.MaxExtents} {scaled.MinExtents} r {scaled.BoundingRadius}");
        for (int i = 0; i < original.Vertices.Count; i += 97)
        {
            Assert.True(Vector3.Distance(original.Vertices[i].Position * 1.5f, scaled.Vertices[i].Position) < 0.01f);
            Assert.True(Vector3.Distance(original.Vertices[i].Normal, scaled.Vertices[i].Normal) < 0.01f, $"normal {i}: {original.Vertices[i].Normal} vs {scaled.Vertices[i].Normal}");
        }
        Assert.True(Vector3.Distance(original.Points[0].Position * 1.5f, scaled.Points[0].Position) < 0.01f);
    }

    [Fact]
    public void Factor_comes_from_target_size_and_global_scale()
    {
        var mesh = MeshData.Parse(["MeshData", "	MaxBoundingExtents [ 10.0 5.0 40.0 ]", "	MinBoundingExtents [ -10.0 -5.0 -40.0 ]"]);
        Assert.Equal(80f, MeshScaler.Size(mesh, MeshAxis.Longest));
        Assert.Equal(20f, MeshScaler.Size(mesh, MeshAxis.Width));
        // 160 m at 2 units per meter = 320 units, from 80 units now: 4×.
        Assert.Equal(4f, MeshScaler.FactorFor(mesh, MeshAxis.Length, 160, 2));
        Assert.Equal(0.5f, MeshScaler.FactorFor(mesh, MeshAxis.Height, 5, 1));
        Assert.Null(MeshScaler.FactorFor(mesh, MeshAxis.Length, 0, 1));
    }
}
