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
    /// Writes <paramref name="meshName"/> scaled by <paramref name="factor"/> to the mod as <paramref name="targetName"/>
    /// (the same name overrides the mesh; another name makes a copy). Returns the written relative path.
    /// </summary>
    public static async Task<string> ScaleAsync(ModWorkspace workspace, string meshName, float factor, string targetName, CancellationToken ct = default)
    {
        if (!(factor > 0) || float.IsInfinity(factor)) throw new ArgumentOutOfRangeException(nameof(factor), "The factor must be a positive number.");
        var mod = workspace.Stack.EditedMod ?? throw new InvalidOperationException("No mod is open for editing.");
        if (!workspace.Index.Meshes.TryGetValue(Path.GetFileNameWithoutExtension(meshName), out var source))
            throw new InvalidOperationException($"Mesh '{meshName}' doesn't exist.");

        var doc = SinsDocument.Load(await workspace.Cache.GetTextPathAsync(source.AbsolutePath, ct));
        Scale(doc, factor);

        string relative = $"Mesh/{targetName}{Path.GetExtension(source.RelativePath)}";
        string target = Path.Combine(mod.RootPath, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        bool binary = SinsFileFormatDetector.Detect(source.AbsolutePath) == SinsFileFormat.Binary;
        if (binary)
        {
            string text = Path.Combine(Path.GetTempPath(), "SinsEditor", Guid.NewGuid().ToString("N") + ".mesh");
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
        mod.Refresh();
        return relative;
    }
}
