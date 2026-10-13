using SinsEditor.Core;
using SinsEditor.Core.FileSystem;
using SinsEditor.Core.Textures;

namespace SinsEditor.Tests.Textures;

/// <summary>Brush edits applied to a throwaway mod over the real base game, checked by rendering after reopening.</summary>
public sealed class BrushEditorTests : IAsyncLifetime
{
    private readonly string _mod = Path.Combine(Path.GetTempPath(), "SinsEditorTests", "BrushMod-" + Guid.NewGuid().ToString("N"));
    private ModWorkspace? _workspace;
    private PixelImage _red = null!, _blue = null!;

    public async Task InitializeAsync()
    {
        if (TestPaths.GameDir is not { } game) return;
        Directory.CreateDirectory(Path.Combine(_mod, "Window"));
        Directory.CreateDirectory(Path.Combine(_mod, "Textures"));
        File.Copy(Path.Combine(game, "entity.manifest"), Path.Combine(_mod, "Entity.manifest"));

        // The mod's brush manifest = the game's plus one own file with two brushes in one own atlas.
        var manifest = File.ReadAllText(Path.Combine(game, "brush.manifest"));
        int count = int.Parse(System.Text.RegularExpressions.Regex.Match(manifest, @"brushFileCount (\d+)").Groups[1].Value);
        manifest = manifest.Replace($"brushFileCount {count}", $"brushFileCount {count + 1}") + "brushFile \"Test.brushes\"\r\n";
        File.WriteAllText(Path.Combine(_mod, "brush.manifest"), manifest);

        _red = Solid(40, 30, 220, 20, 20);
        _blue = Solid(40, 30, 20, 20, 220);
        var atlas = AtlasPacker.Compose(128, 64, null, [(_red, new PixelBox(2, 2, 40, 30)), (_blue, new PixelBox(50, 2, 40, 30))], 2);
        File.WriteAllBytes(Path.Combine(_mod, "Textures", "TestAtlas.tga"), TextureFormats.Encode(atlas, TextureFileFormat.Tga32));
        File.WriteAllText(Path.Combine(_mod, "Window", "Test.brushes"),
            "TXT\r\nonlyLoadOnDemand FALSE\r\nbrushCount 2\r\n" +
            "brush\r\n\tname \"TEST_RED\"\r\n\tcontent \"Simple\"\r\n\tfileName \"TestAtlas\"\r\n\tpixelBox [ 2 , 2 , 40 , 30 ]\r\n" +
            "brush\r\n\tname \"TEST_BLUE\"\r\n\tcontent \"Simple\"\r\n\tfileName \"TestAtlas\"\r\n\tpixelBox [ 50 , 2 , 40 , 30 ]\r\n");
        _workspace = await Open();
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_mod)) Directory.Delete(_mod, recursive: true);
        return Task.CompletedTask;
    }

    private Task<ModWorkspace> Open() => ModWorkspace.OpenAsync(new WorkspaceOptions(TestPaths.GameDir!, _mod, [], "English"),
        cacheRoot: Path.Combine(Path.GetTempPath(), "SinsEditorTests", "cache"));

    private ModWorkspace Workspace
    {
        get
        {
            Skip.If(_workspace is null, "Game directory not configured.");
            return _workspace!;
        }
    }

    private static PixelImage Solid(int w, int h, byte r, byte g, byte b)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255; }
        return new PixelImage(w, h, px);
    }

    private static void AssertSame(PixelImage expected, PixelImage? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal((expected.Width, expected.Height), (actual!.Width, actual.Height));
        Assert.Equal(expected.Bgra, actual.Bgra);
    }

    [SkippableFact]
    public async Task Editing_a_mod_brush_repacks_its_atlas_and_keeps_the_neighbour()
    {
        var ws = Workspace;
        var green = Solid(60, 20, 20, 220, 20);

        var plan = await BrushEditor.PlanAsync(ws, new BrushEditRequest("TEST_RED", false,
            new Dictionary<string, PixelImage> { [Brush.SimpleState] = green }, "TestAtlas", false));
        Assert.Equal(AtlasMode.Repack, plan.Mode);
        plan.Changes.Apply(ws);

        var after = await Open();
        AssertSame(green, after.Textures.RenderBrush("TEST_RED").Image);
        AssertSame(_blue, after.Textures.RenderBrush("TEST_BLUE").Image);
        Assert.Null(after.Textures.RenderBrush("TEST_RED").Error);
    }

    [SkippableFact]
    public async Task New_state_brush_gets_all_states_and_raises_the_count()
    {
        var ws = Workspace;
        var normal = Solid(47, 33, 200, 160, 40);
        var images = BrushStates.All.ToDictionary(s => s, s => ImageImport.DeriveState(normal, s));

        var plan = await BrushEditor.PlanAsync(ws, new BrushEditRequest("TEST_NEW_ICON", true, images, "TestAtlas", false, BrushesFile: "Window/Test.brushes"));
        plan.Changes.Apply(ws);

        var after = await Open();
        foreach (var (state, image) in images) AssertSame(image, after.Textures.RenderBrush("TEST_NEW_ICON", state).Image);
        AssertSame(_red, after.Textures.RenderBrush("TEST_RED").Image);
        Assert.Contains("brushCount 3", File.ReadAllText(Path.Combine(_mod, "Window", "Test.brushes")));
        Assert.Equal("States", Brush.FromEntry(after.Index.Brushes.Entries["TEST_NEW_ICON"]).Content);
    }

    [SkippableFact]
    public async Task Editing_a_base_game_brush_only_adds_to_its_atlas()
    {
        var ws = Workspace;
        // A base-game brush in a TGA atlas that brushes in another base-game .brushes file use too: those can't be
        // rewritten, so their regions must stay where they are.
        var users = ws.Index.Brushes.Entries.Values
            .SelectMany(e => Brush.FromEntry(e).Images.Values.Where(i => i.Box.Width > 0)
                .Select(i => (Entry: e, Texture: Path.GetFileNameWithoutExtension(i.FileName))))
            .Where(x => x.Entry.File.Layer.Kind == LayerKind.BaseGame)
            .ToList();
        var (brush, texture) = users
            .Where(x => Brush.FromEntry(x.Entry).Images.Count == 1
                        && ws.Index.Textures.TryGetValue(x.Texture, out var t) && t.RelativePath.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
                        && users.Any(o => o.Texture.Equals(x.Texture, StringComparison.OrdinalIgnoreCase) && o.Entry.File.RelativePath != x.Entry.File.RelativePath))
            .Select(x => (x.Entry.Name, x.Texture))
            .First();
        var neighbours = ws.Index.Brushes.Entries.Values
            .Where(e => e.Name != brush && Brush.FromEntry(e).Images.Values.Any(i => i.Box.Width > 0
                        && string.Equals(Path.GetFileNameWithoutExtension(i.FileName), texture, StringComparison.OrdinalIgnoreCase)))
            .Take(20)
            .ToDictionary(e => e.Name, e => ws.Textures.RenderBrush(e.Name).Image);
        Assert.NotEmpty(neighbours);
        var icon = Solid(30, 30, 10, 200, 200);

        var plan = await BrushEditor.PlanAsync(ws, new BrushEditRequest(brush, false,
            new Dictionary<string, PixelImage> { [Brush.SimpleState] = icon }, texture, false));
        Assert.Equal(AtlasMode.Append, plan.Mode);
        Assert.Contains(plan.Changes.Notes, n => n.Contains("every existing image stays"));
        plan.Changes.Apply(ws);

        var after = await Open();
        AssertSame(icon, after.Textures.RenderBrush(brush).Image);
        foreach (var (name, image) in neighbours) AssertSame(image!, after.Textures.RenderBrush(name).Image);
        Assert.Equal(LayerKind.EditedMod, after.Index.Textures[texture].Layer.Kind);
    }

    [SkippableFact]
    public async Task New_brush_in_a_new_texture_and_file_is_registered()
    {
        var ws = Workspace;
        var icon = Solid(64, 64, 250, 250, 250);
        Assert.Null(BrushEditor.ValidateNewTexture(ws, "MyNewAtlas"));
        Assert.NotNull(BrushEditor.ValidateNewName(ws, "TEST_RED"));

        var plan = await BrushEditor.PlanAsync(ws, new BrushEditRequest("MY_ICON", true,
            new Dictionary<string, PixelImage> { [Brush.SimpleState] = icon }, "MyNewAtlas", true, TextureFileFormat.Tga32, "Window/MyIcons.brushes"));
        plan.Changes.Apply(ws);

        var after = await Open();
        AssertSame(icon, after.Textures.RenderBrush("MY_ICON").Image);
        Assert.Contains("brushFile \"MyIcons.brushes\"", File.ReadAllText(Path.Combine(_mod, "brush.manifest")));
        Assert.Equal(TextureFileFormat.Tga32, TextureFormats.Detect(Path.Combine(_mod, "Textures", "MyNewAtlas.tga")));
    }
}
