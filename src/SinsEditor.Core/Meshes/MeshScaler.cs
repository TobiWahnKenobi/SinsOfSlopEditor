using System.Globalization;
using SinsEditor.Core.Conversion;
using SinsEditor.Core.Documents;
using SinsEditor.Core.Editing;
using SinsEditor.Core.Validation;

namespace SinsEditor.Core.Meshes;

/// <summary>The dimension a target size refers to. Sins ships point along Z.</summary>
public enum MeshAxis { Longest, Length, Width, Height }

/// <summary>
/// Scales a mesh uniformly: vertex and hardpoint positions, the bounding radius and extents. Normals, tangents and
/// orientations don't change under uniform scaling. The result goes into the edited mod, in the source mesh's format.
/// </summary>
public static class MeshScaler
{
    /// <summary>The mesh's size along an axis, in game units, from its bounding extents.</summary>
    public static float Size(MeshData mesh, MeshAxis axis)
    {
        var d = mesh.MaxExtents - mesh.MinExtents;
        return axis switch
        {
            MeshAxis.Length => d.Z,
            MeshAxis.Width => d.X,
            MeshAxis.Height => d.Y,
            _ => Math.Max(d.X, Math.Max(d.Y, d.Z)),
        };
    }

    /// <summary>
    /// The factor that gives the mesh <paramref name="targetMeters"/> along <paramref name="axis"/> at the global scale of
    /// <paramref name="unitsPerMeter"/> game units per meter; null if the mesh has no extent there.
    /// </summary>
    public static float? FactorFor(MeshData mesh, MeshAxis axis, double targetMeters, double unitsPerMeter)
    {
        float size = Size(mesh, axis);
        if (size <= 0 || targetMeters <= 0 || unitsPerMeter <= 0) return null;
        return (float)(targetMeters * unitsPerMeter / size);
    }

    public static string? ValidateName(ModWorkspace workspace, string name, bool isCopy)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a mesh name.";
        if (name.IndexOfAny([.. Path.GetInvalidFileNameChars(), ' ']) >= 0) return "Use letters, digits and underscores, without spaces.";
        if (isCopy && workspace.Index.Meshes.ContainsKey(name)) return "A mesh with this name already exists.";
        return null;
    }

    /// <summary>Scales the mesh's text in place.</summary>
    public static void Scale(SinsDocument doc, float factor)
    {
        foreach (var line in doc.Lines)
        {
            switch (line.Key)
            {
                case "Position" or "MaxBoundingExtents" or "MinBoundingExtents" when SinsValue.TryGetVector(line.Value, out var v) && v.Length == 3:
                    line.SetRawValue($"[ {F(v[0] * factor)} {F(v[1] * factor)} {F(v[2] * factor)} ]");
                    break;
                case "BoundingRadius" when SinsValue.TryGetDouble(line.Value, out double r):
                    line.SetRawValue(F(r * factor));
                    break;
            }
        }
    }

    private static string F(double value) => value.ToString("0.000000", CultureInfo.InvariantCulture);

    /// <summary>
    /// The suffix copies of attached particles and their meshes get: what the copy's name adds to the mesh's name
    /// (OmegaM → OmegaM_Big gives _Big), or _ plus the copy's name if it doesn't start with it.
    /// </summary>
    public static string CopySuffix(string meshName, string targetName) =>
        targetName.StartsWith(meshName, StringComparison.OrdinalIgnoreCase) && targetName.Length > meshName.Length
            ? targetName[meshName.Length..]
            : "_" + targetName;

    /// <summary>What scaling would write besides the mesh: attached particles and their meshes, under their new names.</summary>
    public sealed record ScalePlan(string Mesh, IReadOnlyList<(string From, string To)> Particles, IReadOnlyList<(string From, string To)> ParticleMeshes);

    public static async Task<ScalePlan> PlanAsync(ModWorkspace workspace, MeshData mesh, string meshName, string targetName, bool copy, CancellationToken ct = default)
    {
        string suffix = copy ? CopySuffix(meshName, targetName) : "";
        var attached = (await MeshLoader.AttachedParticlesAsync(workspace, mesh, ct)).Where(a => a.File != null).ToList();
        var particles = attached.Select(a => (a.Name, a.Name + suffix)).ToList();
        var meshes = attached.SelectMany(a => a.MeshFileNames).Select(Path.GetFileNameWithoutExtension).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(m => workspace.Index.Meshes.ContainsKey(m))
            .Select(m => (m, m + suffix)).ToList();
        return new ScalePlan(targetName, particles, meshes);
    }

    /// <summary>
    /// Writes <paramref name="meshName"/> scaled by <paramref name="factor"/> to the mod as <paramref name="targetName"/>
    /// (the same name overrides the mesh; another name makes a copy). Particles attached through <c>Flair-</c> hardpoints
    /// go along: their meshes get the same factor and their emitters' positions scale too. A copy copies them with the
    /// same suffix and points its hardpoints and emitters at the copies, so the originals stay as they are for others.
    /// Returns the written relative paths.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ScaleAsync(ModWorkspace workspace, string meshName, float factor, string targetName,
        CancellationToken ct = default)
    {
        if (!(factor > 0) || float.IsInfinity(factor)) throw new ArgumentOutOfRangeException(nameof(factor), "The factor must be a positive number.");
        var mod = workspace.Stack.EditedMod ?? throw new InvalidOperationException("No mod is open for editing.");
        string key = Path.GetFileNameWithoutExtension(meshName);
        if (!workspace.Index.Meshes.TryGetValue(key, out var source)) throw new InvalidOperationException($"Mesh '{meshName}' doesn't exist.");
        bool copy = !string.Equals(key, targetName, StringComparison.OrdinalIgnoreCase);

        var doc = SinsDocument.Load(await workspace.Cache.GetTextPathAsync(source.AbsolutePath, ct));
        var plan = await PlanAsync(workspace, MeshData.Parse(doc.Lines.Select(l => l.Text)), key, targetName, copy, ct);
        if (copy)
        {
            var taken = plan.Particles.Where(p => workspace.Index.Particles.ContainsKey(p.To)).Select(p => p.To + ".particle")
                .Concat(plan.ParticleMeshes.Where(m => workspace.Index.Meshes.ContainsKey(m.To)).Select(m => m.To + ".mesh")).ToList();
            if (taken.Count > 0) throw new InvalidOperationException($"These already exist: {string.Join(", ", taken)}. Choose another name.");
        }

        var written = new List<string>();
        Scale(doc, factor);
        var renamedParticles = plan.Particles.ToDictionary(p => p.From, p => p.To, StringComparer.OrdinalIgnoreCase);
        if (copy)
        {
            foreach (var line in doc.Lines.Where(l => l.Key == "DataString"))
            {
                if (SinsValue.TryGetString(line.Value, out var point) && point.StartsWith("Flair-", StringComparison.OrdinalIgnoreCase)
                    && renamedParticles.TryGetValue(point[6..], out var to))
                    line.SetRawValue(SinsValue.FormatString("Flair-" + to));
            }
        }
        written.Add(await WriteLikeAsync(workspace, doc, source, $"Mesh/{targetName}{Path.GetExtension(source.RelativePath)}", ct));

        var renamedMeshes = plan.ParticleMeshes.ToDictionary(m => m.From, m => m.To, StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in plan.Particles)
        {
            var file = workspace.Index.Particles[from];
            var particle = SinsDocument.Load(await workspace.Cache.GetTextPathAsync(file.AbsolutePath, ct));
            ScaleParticle(particle, factor, renamedMeshes);
            written.Add(await WriteLikeAsync(workspace, particle, file, $"Particle/{to}{Path.GetExtension(file.RelativePath)}", ct));
        }
        foreach (var (from, to) in plan.ParticleMeshes)
        {
            var file = workspace.Index.Meshes[from];
            var mesh = SinsDocument.Load(await workspace.Cache.GetTextPathAsync(file.AbsolutePath, ct));
            Scale(mesh, factor);
            written.Add(await WriteLikeAsync(workspace, mesh, file, $"Mesh/{to}{Path.GetExtension(file.RelativePath)}", ct));
        }
        mod.Refresh();
        return written;
    }

    /// <summary>
    /// Scales where a particle's emitters sit (their first Position) and points mesh emitters at renamed meshes.
    /// The meshes themselves are scaled separately.
    /// </summary>
    public static void ScaleParticle(SinsDocument doc, float factor, IReadOnlyDictionary<string, string> renamedMeshes)
    {
        bool inEmitter = false, positioned = false;
        foreach (var line in doc.Lines)
        {
            switch (line.Key)
            {
                case "EmitterContents": inEmitter = true; positioned = false; break;
                case "NumAffectors": inEmitter = false; break;
                case "Position" when inEmitter && !positioned && SinsValue.TryGetVector(line.Value, out var v) && v.Length == 3:
                    line.SetRawValue($"[ {F(v[0] * factor)} {F(v[1] * factor)} {F(v[2] * factor)} ]");
                    positioned = true;
                    break;
                case "MeshName" when inEmitter && SinsValue.TryGetString(line.Value, out var mesh) && mesh.Length > 0
                                     && renamedMeshes.TryGetValue(Path.GetFileNameWithoutExtension(mesh), out var to):
                    line.SetRawValue(SinsValue.FormatString(to + Path.GetExtension(mesh)));
                    break;
            }
        }
    }

    /// <summary>Writes a document into the mod in the same format as <paramref name="source"/> (binary through ConvertData).</summary>
    private static async Task<string> WriteLikeAsync(ModWorkspace workspace, SinsDocument doc, FileSystem.ResolvedFile source, string relative, CancellationToken ct)
    {
        var mod = workspace.Stack.EditedMod!;
        string target = Path.Combine(mod.RootPath, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (SinsFileFormatDetector.Detect(source.AbsolutePath) == SinsFileFormat.Binary)
        {
            string text = Path.Combine(Path.GetTempPath(), "SinsEditor", Guid.NewGuid().ToString("N") + Path.GetExtension(relative));
            string temp = target + SafeWrite.TempSuffix;
            Directory.CreateDirectory(Path.GetDirectoryName(text)!);
            try
            {
                doc.Save(text);
                await ConvertDataTool.FromGameDirectory(workspace.Options.GameDirectory).ConvertAsync(text, temp, ConvertTarget.Binary, ct);
                File.Move(temp, target, overwrite: true);
            }
            finally
            {
                File.Delete(text);
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        else
        {
            doc.ApplyConvention(ConventionCheck.ExpectedConvention(workspace.Stack, relative, doc.HasNonAsciiText));
            SafeWrite.Text(doc, target);
        }
        return relative;
    }
}
