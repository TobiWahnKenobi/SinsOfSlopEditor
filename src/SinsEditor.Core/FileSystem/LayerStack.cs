namespace SinsEditor.Core.FileSystem;

/// <summary>A file as the game sees it: the version from the highest layer that has it.</summary>
public sealed record ResolvedFile(string RelativePath, Layer Layer, string AbsolutePath, IReadOnlyList<Layer> OverriddenLayers)
{
    public bool IsOverride => OverriddenLayers.Count > 0;
}

/// <summary>
/// Base game at the bottom, base mods above it, the edited mod on top.
/// A file in a higher layer replaces the same path in all lower layers.
/// </summary>
public sealed class LayerStack
{
    public LayerStack(IEnumerable<Layer> lowestFirst)
    {
        Layers = lowestFirst.ToList();
        if (Layers.Count(l => l.IsEditable) > 1) throw new ArgumentException("Only one layer can be the edited mod.");
    }

    /// <summary>Lowest (base game) first.</summary>
    public IReadOnlyList<Layer> Layers { get; }

    public Layer? EditedMod => Layers.FirstOrDefault(l => l.IsEditable);

    public Layer? BaseGame => Layers.FirstOrDefault(l => l.Kind == LayerKind.BaseGame);

    public ResolvedFile? Resolve(string relativePath)
    {
        string path = Layer.Normalize(relativePath);
        for (int i = Layers.Count - 1; i >= 0; i--)
        {
            if (Layers[i].GetAbsolutePath(path) is { } abs)
            {
                var overridden = Layers.Take(i).Where(l => l.Contains(path)).Reverse().ToList();
                return new ResolvedFile(path, Layers[i], abs, overridden);
            }
        }
        return null;
    }

    /// <summary>Effective files in a folder (non-recursive), optionally filtered by extension like ".entity".</summary>
    public IEnumerable<ResolvedFile> EnumerateFolder(string folder, string? extension = null)
    {
        string prefix = Layer.Normalize(folder).TrimEnd('/') + "/";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in Layers.Reverse())
        {
            foreach (string path in layer.Files.Keys)
            {
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (path.IndexOf('/', prefix.Length) >= 0) continue;
                if (extension != null && !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Add(path)) yield return Resolve(path)!;
            }
        }
    }

    public void Refresh()
    {
        foreach (var layer in Layers) layer.Refresh();
    }
}
