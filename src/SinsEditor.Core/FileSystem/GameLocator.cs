using System.Text.RegularExpressions;
using SinsEditor.Core.Conversion;

namespace SinsEditor.Core.FileSystem;

/// <summary>Finds the game install and the mods folders on this machine.</summary>
public static partial class GameLocator
{
    public const string GameFolderName = "Sins of a Solar Empire Rebellion";

    public static bool IsGameDirectory(string? path) =>
        !string.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, ConvertDataTool.ExeName));

    /// <summary>Looks in every Steam library listed in Steam's libraryfolders.vdf.</summary>
    public static string? FindGameDirectory(string? steamPath)
    {
        if (string.IsNullOrEmpty(steamPath)) return null;
        var libraries = new List<string> { steamPath };
        string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (Match m in LibraryPathRegex().Matches(File.ReadAllText(vdf)))
                libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }
        return libraries
            .Select(lib => Path.Combine(lib, "steamapps", "common", GameFolderName))
            .FirstOrDefault(IsGameDirectory);
    }

    /// <summary>
    /// Mods folders under <c>Documents\my games\Ironclad Games\Sins of a Solar Empire Rebellion</c>,
    /// one per game version (e.g. <c>Mods-Rebellion v1.85 Dev</c>), newest name first.
    /// </summary>
    public static IReadOnlyList<string> FindModsFolders()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "my games", "Ironclad Games", GameFolderName);
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root, "Mods-*").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static readonly string[] ModDataFolders =
        ["GameInfo", "Window", "Textures", "Mesh", "Particle", "String", "Sound", "PipelineEffect", "Galaxy", "Font", "TextureAnimations"];

    /// <summary>
    /// Mod folders in a mods folder. A mod only needs the files it overrides (e.g. an AI mod with just GameInfo and
    /// String), so any subfolder with a manifest or one of the game's data folders counts.
    /// </summary>
    public static IReadOnlyList<string> ListMods(string modsFolder) =>
        Directory.Exists(modsFolder)
            ? Directory.EnumerateDirectories(modsFolder)
                .Where(IsModFolder)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

    public static bool IsModFolder(string path) =>
        Directory.EnumerateFiles(path, "*.manifest").Any()
        || ModDataFolders.Any(f => Directory.Exists(Path.Combine(path, f)));

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex LibraryPathRegex();
}
