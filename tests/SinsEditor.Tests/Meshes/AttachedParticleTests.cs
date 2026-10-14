using System.Numerics;
using SinsEditor.Core;
using SinsEditor.Core.Documents;
using SinsEditor.Core.Meshes;

namespace SinsEditor.Tests.Meshes;

/// <summary>
/// OmegaM.mesh of the test mod has a <c>Flair-OmegaRotate</c> hardpoint; OmegaRotate.particle shows OmegaR.mesh.
/// The test mod is a read-only base mod under a throwaway mod, so nothing of it changes.
/// </summary>
public sealed class AttachedParticleTests : IAsyncLifetime
{
    private readonly string _mod = Path.Combine(Path.GetTempPath(), "SinsEditorTests", "FlairMod-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(_mod)) Directory.Delete(_mod, recursive: true);
        return Task.CompletedTask;
    }

    private Task<ModWorkspace> Open() => ModWorkspace.OpenAsync(new WorkspaceOptions(TestPaths.GameDir!, _mod, [TestPaths.TestMod!], "English"),
        cacheRoot: Path.Combine(Path.GetTempPath(), "SinsEditorTests", "cache"));

    private async Task<ModWorkspace> Workspace()
    {
        Skip.If(TestPaths.GameDir is null || TestPaths.TestMod is null, "Game directory or test mod not configured.");
        Skip.IfNot(File.Exists(Path.Combine(TestPaths.TestMod!, "Mesh", "OmegaM.mesh")), "Test mod has no OmegaM.mesh.");
        Directory.CreateDirectory(_mod);
        return await Open();
    }

    [SkippableFact]
    public async Task Flair_hardpoints_bring_their_particle_meshes()
    {
        var ws = await Workspace();
        var omega = (await MeshLoader.LoadAsync(ws, "OmegaM"))!;
        var particles = await MeshLoader.AttachedParticlesAsync(ws, omega.Data);
        Assert.Contains(particles, p => p.Name == "OmegaRotate" && p.MeshFileNames.Contains("OmegaR.mesh"));

        var attached = await MeshLoader.AttachedMeshesAsync(ws, omega.Data);
        var rotor = Assert.Single(attached, a => a.Mesh.Name == "OmegaR");
        var point = omega.Data.Points.First(p => p.Name == "Flair-OmegaRotate");
        Assert.True(Vector3.Distance(point.Position, rotor.Placement.Translation) < 0.001f);
    }

    [SkippableFact]
    public async Task Scaling_a_copy_copies_and_scales_the_particle_and_its_mesh()
    {
        var ws = await Workspace();
        var omegaR = (await MeshLoader.LoadAsync(ws, "OmegaR"))!.Data;

        var written = await MeshScaler.ScaleAsync(ws, "OmegaM", 2f, "OmegaM_Big");
        Assert.Contains("Mesh/OmegaM_Big.mesh", written, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(written, w => w.StartsWith("Particle/OmegaRotate_Big", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(written, w => w.StartsWith("Mesh/OmegaR_Big", StringComparison.OrdinalIgnoreCase));

        var after = await Open();
        var big = (await MeshLoader.LoadAsync(after, "OmegaM_Big"))!.Data;
        Assert.Contains(big.Points, p => p.Name == "Flair-OmegaRotate_Big");
        Assert.DoesNotContain(big.Points, p => p.Name == "Flair-OmegaRotate");

        var particle = await MeshLoader.LoadParticleAsync(after, after.Index.Particles["OmegaRotate_Big"]);
        Assert.Contains(particle.Emitters, e => e.MeshName.Equals("OmegaR_Big.mesh", StringComparison.OrdinalIgnoreCase));

        var rotor = (await MeshLoader.LoadAsync(after, "OmegaR_Big"))!.Data;
        Assert.True(Vector3.Distance(omegaR.MaxExtents * 2f, rotor.MaxExtents) < 0.01f);
        Assert.Equal(omegaR.BoundingRadius * 2f, rotor.BoundingRadius, 0.01f);

        // The originals stay as they were.
        Assert.False(after.Index.Particles["OmegaRotate"].Layer.IsEditable);
        Assert.False(after.Index.Meshes["OmegaR"].Layer.IsEditable);
        var attached = await MeshLoader.AttachedMeshesAsync(after, big);
        Assert.Contains(attached, a => a.Mesh.Name == "OmegaR_Big");
    }

    [Fact]
    public void Particle_emitters_are_read_and_scaled()
    {
        string[] text =
        [
            "TXT2", "ParticleSimulation", "\tNumEmitters 1", "\tEmitterType \"Point\"", "\tEmitterContents", "\t\tName \"Rotor\"",
            "\t\tMeshName \"Rotor.mesh\"", "\t\tPosition [ 1.000000 2.000000 3.000000 ]", "\t\tOrientation",
            "\t\t\t [ 0.000000 0.000000 1.000000 ]", "\t\t\t [ 0.000000 1.000000 0.000000 ]", "\t\t\t [ -1.000000 0.000000 0.000000 ]",
            "\tNumAffectors 1", "\tAffectorType \"LinearForceToPoint\"", "\tAffectorContents", "\t\tPosition [ 9.000000 9.000000 9.000000 ]",
        ];
        var emitter = Assert.Single(ParticleData.Parse(text).Emitters);
        Assert.Equal(("Rotor", "Rotor.mesh", new Vector3(1, 2, 3)), (emitter.Name, emitter.MeshName, emitter.Position));
        Assert.Equal(-1f, emitter.Orientation.M31);

        var doc = SinsDocument.Parse(System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n", text) + "\r\n"));
        MeshScaler.ScaleParticle(doc, 2f, new Dictionary<string, string> { ["Rotor"] = "Rotor_Big" });
        string scaled = string.Concat(doc.Lines.Select(l => l.Text + "\n"));
        Assert.Contains("MeshName \"Rotor_Big.mesh\"", scaled);
        Assert.Contains("Position [ 2.000000 4.000000 6.000000 ]", scaled);
        Assert.Contains("Position [ 9.000000 9.000000 9.000000 ]", scaled);
        Assert.Equal("_Big", MeshScaler.CopySuffix("OmegaM", "OmegaM_Big"));
        Assert.Equal("_Huge", MeshScaler.CopySuffix("OmegaM", "Huge"));
    }
}
