namespace SinsEditor.Core.FileSystem;

/// <summary>
/// Where the string table for a language lives. Mods use <c>String/&lt;Language&gt;.str</c>; the base game keeps
/// its text in <c>Localizations/&lt;code&gt;/String/English.str</c> (every language's file is named English.str).
/// </summary>
public static class Languages
{
    private static readonly Dictionary<string, string> GameCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["English"] = "en",
        ["German"] = "de",
        ["French"] = "fr",
        ["Italian"] = "it",
        ["Japanese"] = "ja",
        ["Polish"] = "pl",
        ["Russian"] = "ru",
        ["Chinese"] = "zh-CN",
    };

    /// <summary>
    /// Languages the user can pick: the <c>String/*.str</c> files of the mod layers.
    /// If no mod layer has any, the base game's localizations.
    /// </summary>
    public static IReadOnlyList<string> Available(LayerStack stack)
    {
        var fromMods = stack.Layers
            .Where(l => l.Kind != LayerKind.BaseGame)
            .SelectMany(l => l.Files.Keys)
            .Where(p => p.StartsWith("String/", StringComparison.OrdinalIgnoreCase)
                        && p.EndsWith(".str", StringComparison.OrdinalIgnoreCase)
                        && p.IndexOf('/', "String/".Length) < 0)
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (fromMods.Count > 0) return fromMods;

        var game = stack.BaseGame;
        if (game is null) return [];
        return GameCodes
            .Where(kv => game.Contains($"Localizations/{kv.Value}/String/English.str"))
            .Select(kv => kv.Key)
            .ToList();
    }

    /// <summary>String table file for <paramref name="language"/>: from the highest mod layer that has it, else the base game's localization.</summary>
    public static ResolvedFile? ResolveStringFile(LayerStack stack, string language)
    {
        for (int i = stack.Layers.Count - 1; i >= 0; i--)
        {
            var layer = stack.Layers[i];
            string path = StringFilePath(layer, language);
            if (layer.GetAbsolutePath(path) is { } abs)
            {
                var overridden = stack.Layers.Take(i).Where(l => l.Contains(StringFilePath(l, language))).Reverse().ToList();
                return new ResolvedFile(path, layer, abs, overridden);
            }
        }
        return null;
    }

    /// <summary>Where <paramref name="layer"/> would keep the string table for <paramref name="language"/>.</summary>
    public static string StringFilePath(Layer layer, string language) =>
        layer.Kind == LayerKind.BaseGame && GameCodes.TryGetValue(language, out var code)
            ? $"Localizations/{code}/String/English.str"
            : $"String/{language}.str";
}
