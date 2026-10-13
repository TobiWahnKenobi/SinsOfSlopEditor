namespace SinsEditor.Core.FileSystem;

public enum LayerKind
{
    BaseGame,
    BaseMod,
    EditedMod,
}

/// <summary>
/// One source of game files: the base game, a read-only base mod, or the mod being edited.
/// Paths inside a layer are relative, use '/' and compare case-insensitively like Windows.
/// </summary>
public sealed class Layer
{
    /// <summary>Folders of the game install that hold game data (everything else there is tools, launcher, DLLs, …).</summary>
    private static readonly string[] GameDataFolders =
    [
        "Font", "Galaxy", "GameInfo", "Mesh", "Movie", "Particle", "PipelineEffect", "Sound",
        "String", "TextureAnimations", "Textures", "Window", "Localizations",
    ];

    private Dictionary<string, string>? _files;

    public Layer(string name, string rootPath, LayerKind kind)
    {
        Name = name;
        RootPath = Path.GetFullPath(rootPath);
        Kind = kind;
    }

    public string Name { get; }
    public string RootPath { get; }
    public LayerKind Kind { get; }
    public bool IsEditable => Kind == LayerKind.EditedMod;

    /// <summary>Relative path → absolute path of every data file in this layer. Scanned once; see <see cref="Refresh"/>.</summary>
    public IReadOnlyDictionary<string, string> Files => _files ??= Scan();

    public bool Contains(string relativePath) => Files.ContainsKey(Normalize(relativePath));

    public string? GetAbsolutePath(string relativePath) =>
        Files.TryGetValue(Normalize(relativePath), out var abs) ? abs : null;

    public void Refresh() => _files = null;

    public static string Normalize(string relativePath) => relativePath.Replace('\\', '/').TrimStart('/');

    private Dictionary<string, string> Scan()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(RootPath)) return files;

        if (Kind == LayerKind.BaseGame)
        {
            foreach (string file in Directory.EnumerateFiles(RootPath, "*.manifest"))
                Add(files, file);
            foreach (string folder in GameDataFolders)
            {
                string dir = Path.Combine(RootPath, folder);
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    Add(files, file);
            }
        }
        else
        {
            foreach (string file in Directory.EnumerateFiles(RootPath, "*", SearchOption.AllDirectories))
                Add(files, file);
        }
        return files;
    }

    private void Add(Dictionary<string, string> files, string absolute) =>
        files[Normalize(Path.GetRelativePath(RootPath, absolute))] = absolute;

    public override string ToString() => $"{Name} ({Kind})";
}
