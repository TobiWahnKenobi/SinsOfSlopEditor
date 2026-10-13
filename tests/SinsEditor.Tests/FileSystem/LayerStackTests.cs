using SinsEditor.Core.FileSystem;

namespace SinsEditor.Tests.FileSystem;

public sealed class LayerStackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SinsEditorTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Write(string relative, string content = "TXT\n")
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private LayerStack Stack() => new([
        new Layer("Game", Path.Combine(_root, "game"), LayerKind.BaseGame),
        new Layer("Base", Path.Combine(_root, "mods", "Base"), LayerKind.BaseMod),
        new Layer("Mine", Path.Combine(_root, "mods", "Mine"), LayerKind.EditedMod),
    ]);

    [Fact]
    public void Highest_layer_wins_and_lists_what_it_overrides()
    {
        Write("game/GameInfo/A.entity");
        Write("mods/Base/GameInfo/A.entity");
        Write("mods/Mine/GameInfo/a.ENTITY");

        var file = Stack().Resolve("GameInfo/A.entity")!;

        Assert.Equal("Mine", file.Layer.Name);
        Assert.Equal(["Base", "Game"], file.OverriddenLayers.Select(l => l.Name));
    }

    [Fact]
    public void Falls_through_to_lower_layers()
    {
        Write("game/GameInfo/OnlyInGame.entity");

        var file = Stack().Resolve("GameInfo\\OnlyInGame.entity")!;

        Assert.Equal("Game", file.Layer.Name);
        Assert.False(file.IsOverride);
    }

    [Fact]
    public void Game_layer_only_sees_data_folders_and_root_manifests()
    {
        Write("game/GameInfo/A.entity");
        Write("game/entity.manifest");
        Write("game/Galaxy Forge/Tool.entity");
        Write("game/readme.txt");

        var files = Stack().Layers[0].Files.Keys.Order().ToList();

        Assert.Equal(["entity.manifest", "GameInfo/A.entity"], files);
    }

    [Fact]
    public void EnumerateFolder_merges_layers_without_duplicates_or_subfolders()
    {
        Write("game/GameInfo/A.entity");
        Write("game/GameInfo/B.entity");
        Write("mods/Mine/GameInfo/B.entity");
        Write("mods/Mine/GameInfo/C.entity");
        Write("mods/Mine/GameInfo/Sub/D.entity");
        Write("mods/Mine/GameInfo/E.sounddata");

        var files = Stack().EnumerateFolder("GameInfo", ".entity").ToDictionary(f => f.RelativePath, f => f.Layer.Name);

        Assert.Equal(3, files.Count);
        Assert.Equal("Game", files["GameInfo/A.entity"]);
        Assert.Equal("Mine", files["GameInfo/B.entity"]);
        Assert.Equal("Mine", files["GameInfo/C.entity"]);
    }

    [Fact]
    public void Only_one_editable_layer_is_allowed()
    {
        Assert.Throws<ArgumentException>(() => new LayerStack([
            new Layer("A", _root, LayerKind.EditedMod),
            new Layer("B", _root, LayerKind.EditedMod),
        ]));
    }

    [Fact]
    public void Language_comes_from_mod_string_files_and_falls_back_to_game_localization()
    {
        Write("game/Localizations/en/String/English.str");
        Write("game/Localizations/de/String/English.str");
        Write("mods/Base/String/German.str");
        var stack = Stack();

        Assert.Equal(["German"], Languages.Available(stack));
        Assert.Equal("Base", Languages.ResolveStringFile(stack, "German")!.Layer.Name);
        var english = Languages.ResolveStringFile(stack, "English")!;
        Assert.Equal("Game", english.Layer.Name);
        Assert.Equal("Localizations/en/String/English.str", english.RelativePath);
    }

    [Fact]
    public void Game_localizations_are_offered_when_no_mod_has_string_files()
    {
        Write("game/Localizations/en/String/English.str");
        Write("game/Localizations/fr/String/English.str");

        Assert.Equal(["English", "French"], Languages.Available(Stack()));
    }

    [Fact]
    public void Mods_without_a_manifest_are_listed()
    {
        Write("mods/Full/Entity.manifest");
        Write("mods/AiOnly/GameInfo/AI.entity");
        Write("mods/AiOnly/String/English.str");
        Write("mods/Archive/readme.txt");
        Write("mods/SomeMod.7z");

        var mods = GameLocator.ListMods(Path.Combine(_root, "mods")).Select(Path.GetFileName);

        Assert.Equal(["AiOnly", "Full"], mods);
    }

    [Theory]
    [InlineData("Mine", new[] { "Base" })]
    [InlineData("NotListed", new[] { "Base", "Mine", "Other" })]
    [InlineData("Base", new string[0])]
    public void SuggestBaseMods_takes_mods_listed_before_the_edited_one(string edited, string[] expected)
    {
        Write("mods/EnabledMods.txt", "TXT2\nSinsArchiveVersion 197\nVersion 0\nenabledModNameCount 4\n" +
            "enabledModName \"Base\"\nenabledModName \"Mine\"\nenabledModName \"Gone\"\nenabledModName \"Other\"\n");
        Directory.CreateDirectory(Path.Combine(_root, "mods", "Base"));
        Directory.CreateDirectory(Path.Combine(_root, "mods", "Mine"));
        Directory.CreateDirectory(Path.Combine(_root, "mods", "Other"));

        // "Gone" has no folder and is skipped.
        Assert.Equal(expected, EnabledMods.SuggestBaseMods(Path.Combine(_root, "mods"), edited));
    }
}
