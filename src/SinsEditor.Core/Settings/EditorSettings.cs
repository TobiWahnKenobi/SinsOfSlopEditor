using System.Text.Json;
using System.Text.Json.Serialization;

namespace SinsEditor.Core.Settings;

/// <summary>Per-mod choices remembered between sessions. Stored in the editor settings, never in the mod.</summary>
public sealed class ModSettings
{
    /// <summary>Base mod folders, lowest first.</summary>
    public List<string> BaseMods { get; set; } = [];
    public string? Language { get; set; }
}

/// <summary>Editor-wide settings in <c>%AppData%\SinsEditor\settings.json</c>.</summary>
public sealed class EditorSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SinsEditor", "settings.json");

    public string? GameDirectory { get; set; }
    /// <summary>Most recent first.</summary>
    public List<string> RecentMods { get; set; } = [];
    /// <summary>Keyed by full mod path, case-insensitive.</summary>
    public Dictionary<string, ModSettings> Mods { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ModSettings ForMod(string modPath)
    {
        if (!Mods.TryGetValue(modPath, out var settings)) Mods[modPath] = settings = new ModSettings();
        return settings;
    }

    public void AddRecent(string modPath, int max = 10)
    {
        RecentMods.RemoveAll(p => string.Equals(p, modPath, StringComparison.OrdinalIgnoreCase));
        RecentMods.Insert(0, modPath);
        if (RecentMods.Count > max) RecentMods.RemoveRange(max, RecentMods.Count - max);
    }

    public static EditorSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<EditorSettings>(File.ReadAllText(path), JsonOptions);
                if (settings != null)
                {
                    settings.Mods = new Dictionary<string, ModSettings>(settings.Mods, StringComparer.OrdinalIgnoreCase);
                    return settings;
                }
            }
        }
        catch (JsonException)
        {
            // A broken settings file shouldn't stop the editor; start fresh.
        }
        return new EditorSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
