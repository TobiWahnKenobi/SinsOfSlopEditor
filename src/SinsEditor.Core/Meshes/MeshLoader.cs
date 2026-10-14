using System.Numerics;
using SinsEditor.Core.FileSystem;
using SinsEditor.Core.Textures;

namespace SinsEditor.Core.Meshes;

public sealed record LoadedMesh(string Name, ResolvedFile File, MeshData Data);

/// <summary>
/// A mesh shown by a particle that a <c>Flair-</c> hardpoint attaches: where it sits relative to the main mesh
/// (hardpoint placement times emitter placement), and what it came from.
/// </summary>
public sealed record AttachedMesh(string PointName, string Particle, ResolvedFile ParticleFile, string MeshFileName, LoadedMesh Mesh, Matrix4x4 Placement,
    string Effect = "");

/// <summary>A particle effect on a <c>Flair-</c> hardpoint: where it sits on the mesh and what it is.</summary>
public sealed record AttachedEffect(string PointName, string Particle, Matrix4x4 Placement, ParticleEffectDefinition Definition);

/// <summary>A particle attached through a <c>Flair-</c> hardpoint, and the meshes its emitters show.</summary>
public sealed record AttachedParticle(string Name, ResolvedFile? File, IReadOnlyList<string> MeshFileNames);

public static class MeshLoader
{
    /// <summary>Resolves a mesh by name (with or without extension) through the layers and reads it, converting binary meshes.</summary>
    public static async Task<LoadedMesh?> LoadAsync(ModWorkspace workspace, string name, CancellationToken ct = default)
    {
        string key = Path.GetFileNameWithoutExtension(name);
        if (!workspace.Index.Meshes.TryGetValue(key, out var file)) return null;
        string text = await workspace.Cache.GetTextPathAsync(file.AbsolutePath, ct);
        var data = await Task.Run(() => MeshData.Load(text), ct);
        return new LoadedMesh(key, file, data);
    }

    /// <summary>The particles the mesh's <c>Flair-</c> hardpoints attach (each once), with the meshes they show.</summary>
    public static async Task<IReadOnlyList<AttachedParticle>> AttachedParticlesAsync(ModWorkspace workspace, MeshData mesh, CancellationToken ct = default)
    {
        var result = new List<AttachedParticle>();
        foreach (string name in mesh.Points.Select(p => p.FlairParticle).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var file = workspace.Index.Particles.GetValueOrDefault(name);
            var meshes = file is null ? [] : (await LoadParticleAsync(workspace, file, ct)).Emitters
                .Select(e => e.MeshName).Where(m => m.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            result.Add(new AttachedParticle(name, file, meshes));
        }
        return result;
    }

    /// <summary>Every particle mesh attached to the mesh, placed relative to it; missing particles and meshes are skipped.</summary>
    public static async Task<IReadOnlyList<AttachedMesh>> AttachedMeshesAsync(ModWorkspace workspace, MeshData mesh, CancellationToken ct = default)
    {
        var result = new List<AttachedMesh>();
        var particles = new Dictionary<string, ParticleData>(StringComparer.OrdinalIgnoreCase);
        var meshes = new Dictionary<string, LoadedMesh?>(StringComparer.OrdinalIgnoreCase);
        foreach (var point in mesh.Points)
        {
            if (point.FlairParticle is not { } name || workspace.Index.Particles.GetValueOrDefault(name) is not { } file) continue;
            if (!particles.TryGetValue(name, out var particle)) particles[name] = particle = await LoadParticleAsync(workspace, file, ct);
            var pointPlacement = MeshMath.Placement(point.Orientation, point.Position);
            foreach (var emitter in particle.Emitters.Where(e => e.MeshName.Length > 0))
            {
                if (!meshes.TryGetValue(emitter.MeshName, out var loaded)) meshes[emitter.MeshName] = loaded = await LoadAsync(workspace, emitter.MeshName, ct);
                if (loaded is null) continue;
                var placement = MeshMath.Placement(emitter.Orientation, emitter.Position) * pointPlacement;
                result.Add(new AttachedMesh(point.Name, name, file, emitter.MeshName, loaded, placement, emitter.Effect));
            }
        }
        return result;
    }

    /// <summary>Every particle effect on the mesh's <c>Flair-</c> hardpoints (one per hardpoint), for simulating its sprites.</summary>
    public static async Task<IReadOnlyList<AttachedEffect>> AttachedEffectsAsync(ModWorkspace workspace, MeshData mesh, CancellationToken ct = default)
    {
        var result = new List<AttachedEffect>();
        var definitions = new Dictionary<string, ParticleEffectDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var point in mesh.Points)
        {
            if (point.FlairParticle is not { } name || workspace.Index.Particles.GetValueOrDefault(name) is not { } file) continue;
            if (!definitions.TryGetValue(name, out var definition))
            {
                string text = await workspace.Cache.GetTextPathAsync(file.AbsolutePath, ct);
                definitions[name] = definition = await Task.Run(() => ParticleEffectDefinition.Load(text), ct);
            }
            result.Add(new AttachedEffect(point.Name, name, MeshMath.Placement(point.Orientation, point.Position), definition));
        }
        return result;
    }

    public static async Task<ParticleData> LoadParticleAsync(ModWorkspace workspace, ResolvedFile file, CancellationToken ct = default)
    {
        string text = await workspace.Cache.GetTextPathAsync(file.AbsolutePath, ct);
        return await Task.Run(() => ParticleData.Load(text), ct);
    }

    /// <summary>A texture a mesh names (file names carry an extension, the index doesn't).</summary>
    public static ResolvedFile? ResolveTexture(ModWorkspace workspace, string fileName) =>
        fileName.Length == 0 ? null : workspace.Index.Textures.GetValueOrDefault(Path.GetFileNameWithoutExtension(fileName));

    /// <summary>The pixels of a texture a mesh names, or null if it's missing or unreadable.</summary>
    public static PixelImage? LoadTexture(ModWorkspace workspace, string fileName)
    {
        if (ResolveTexture(workspace, fileName) is not { } file) return null;
        try
        {
            return TextureLoader.Load(file.AbsolutePath);
        }
        catch (Exception e) when (e is IOException or TextureLoadException)
        {
            return null;
        }
    }
}
