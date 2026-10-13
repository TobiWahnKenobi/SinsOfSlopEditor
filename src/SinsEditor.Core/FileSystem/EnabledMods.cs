using SinsEditor.Core.Documents;

namespace SinsEditor.Core.FileSystem;

/// <summary>The game's list of enabled mods (<c>EnabledMods.txt</c> in the mods folder). Later entries override earlier ones.</summary>
public static class EnabledMods
{
    public const string FileName = "EnabledMods.txt";

    public static IReadOnlyList<string> Read(string modsFolder)
    {
        string path = Path.Combine(modsFolder, FileName);
        if (!File.Exists(path)) return [];
        return SinsDocument.Load(path).Lines
            .Where(l => l.Key == "enabledModName" && SinsValue.TryGetString(l.Value, out _))
            .Select(l => { SinsValue.TryGetString(l.Value, out var name); return name; })
            .ToList();
    }

    /// <summary>
    /// Base mods to suggest for <paramref name="editedMod"/>, lowest first: the mods listed before it,
    /// or all listed mods if it isn't in the list. Mods whose folder doesn't exist are left out.
    /// </summary>
    public static IReadOnlyList<string> SuggestBaseMods(string modsFolder, string editedMod)
    {
        var enabled = Read(modsFolder);
        int index = enabled.ToList().FindIndex(n => string.Equals(n, editedMod, StringComparison.OrdinalIgnoreCase));
        var before = index >= 0 ? enabled.Take(index) : enabled;
        return before.Where(n => Directory.Exists(Path.Combine(modsFolder, n))).ToList();
    }
}
