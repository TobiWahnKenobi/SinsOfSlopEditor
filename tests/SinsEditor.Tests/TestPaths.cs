using System.Text.Json;

namespace SinsEditor.Tests;

/// <summary>
/// Locations of real game data for corpus tests. Read from environment variables
/// (SINSEDITOR_TEST_MOD, SINSEDITOR_GAME_DIR) or from an untracked <c>testpaths.local.json</c>
/// in the repository root: <c>{ "testMod": "...", "gameDir": "..." }</c>.
/// Tests that need them are skipped when they're missing.
/// </summary>
public static class TestPaths
{
    private static readonly Lazy<Dictionary<string, string>> Local = new(LoadLocal);

    public static string? TestMod => Resolve("SINSEDITOR_TEST_MOD", "testMod");
    public static string? GameDir => Resolve("SINSEDITOR_GAME_DIR", "gameDir");

    private static string? Resolve(string envVar, string jsonKey)
    {
        string? path = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrEmpty(path)) Local.Value.TryGetValue(jsonKey, out path);
        return !string.IsNullOrEmpty(path) && Directory.Exists(path) ? path : null;
    }

    private static Dictionary<string, string> LoadLocal()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string file = Path.Combine(dir.FullName, "testpaths.local.json");
            if (File.Exists(file))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [];
        }
        return [];
    }
}
