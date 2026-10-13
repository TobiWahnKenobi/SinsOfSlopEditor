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

    /// <summary>Mod folders in a mods folder (any subfolder with an entity manifest).</summary>
    public static IReadOnlyList<string> ListMods(string modsFolder) =>
        Directory.Exists(modsFolder)
            ? Directory.EnumerateDirectories(modsFolder)
                .Where(d => File.Exists(Path.Combine(d, "Entity.manifest")))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex LibraryPathRegex();
}
