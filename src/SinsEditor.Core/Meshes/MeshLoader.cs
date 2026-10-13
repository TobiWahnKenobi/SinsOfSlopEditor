using SinsEditor.Core.FileSystem;
using SinsEditor.Core.Textures;

namespace SinsEditor.Core.Meshes;

public sealed record LoadedMesh(string Name, ResolvedFile File, MeshData Data);

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
