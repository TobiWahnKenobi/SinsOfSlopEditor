using SinsEditor.Core;
using SinsEditor.Core.Documents;
using SinsEditor.Core.Editing;
using SinsEditor.Core.FileSystem;

namespace SinsEditor.Tests.Editing;

/// <summary>Strings edited from an entity: a throwaway mod over the base game, so the base game's table is copied in first.</summary>
public sealed class StringEditsTests : IAsyncLifetime
{
    private readonly string _mod = Path.Combine(Path.GetTempPath(), "SinsEditorTests", "StringMod-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(_mod)) Directory.Delete(_mod, recursive: true);
        return Task.CompletedTask;
    }

    private Task<ModWorkspace> Open() => ModWorkspace.OpenAsync(new WorkspaceOptions(TestPaths.GameDir!, _mod, [], "English"),
        cacheRoot: Path.Combine(Path.GetTempPath(), "SinsEditorTests", "cache"));

    [SkippableFact]
    public async Task Changing_and_adding_strings_writes_the_mods_string_file()
    {
        Skip.If(TestPaths.GameDir is null, "Game directory not configured.");
        Directory.CreateDirectory(_mod);
        var ws = await Open();
        var table = ws.Index.Strings;
        string id = table.Entries.First(e => e.Id.StartsWith("IDS_", StringComparison.Ordinal) && !e.Value.Contains('"')).Id;
        int before = table.TotalCount;

        Assert.Equal(table.Get(id), StringEdits.SetString(ws, id, "Changed text"));
        Assert.Null(StringEdits.SetString(ws, "IDS_SINSEDITOR_TEST_NEW", "A new string"));

        // The index sees both at once, and so does a fresh workspace from the written file.
        Assert.Equal("Changed text", ws.Index.Strings.Get(id));
        Assert.Equal("A new string", ws.Index.Strings.Get("IDS_SINSEDITOR_TEST_NEW"));
        string path = Path.Combine(_mod, Languages.StringFilePath(ws.Stack.EditedMod!, "English").Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path));
        var doc = SinsDocument.Load(path);
        Assert.Equal(before + 1, int.Parse(doc.Lines.First(l => l.Key == "NumStrings").Value));

        var after = await Open();
        Assert.Equal("Changed text", after.Index.Strings.Get(id));
        Assert.Equal("A new string", after.Index.Strings.Get("IDS_SINSEDITOR_TEST_NEW"));
        Assert.Equal(before + 1, after.Index.Strings.TotalCount);
        Assert.Throws<ArgumentException>(() => StringEdits.SetString(after, id, "with \"quotes\""));
    }

    [SkippableFact]
    public async Task Scattered_variants_are_grouped_behind_their_first_entry()
    {
        Skip.If(TestPaths.GameDir is null, "Game directory not configured.");
        Directory.CreateDirectory(Path.Combine(_mod, "String"));
        string Block(string id, string value) => $"StringInfo\r\n\tID \"{id}\"\r\n\tValue \"{value}\"\r\n";
        File.WriteAllText(Path.Combine(_mod, "String", "English.str"), "TXT\r\nNumStrings 6\r\n" + Block("IDS_TAUNT", "one") + Block("IDS_OTHER", "x")
            + Block("IDS_TAUNT", "two") + Block("IDS_LAST", "y") + Block("IDS_TAUNT", "three") + Block("IDS_TAUNT", "four"));
        var ws = await Open();
        Assert.Equal(1, Core.Validation.StringFixes.CountScattered(ws.Index.Strings));

        Assert.Equal(3, Core.Validation.StringFixes.GroupScattered(ws));
        var after = await Open();
        var ids = SinsDocument.Load(Path.Combine(_mod, "String", "English.str")).Lines.Where(l => l.Key is "ID" or "Value").Select(l => l.Value).ToList();
        Assert.Equal(["\"IDS_TAUNT\"", "\"one\"", "\"IDS_TAUNT\"", "\"two\"", "\"IDS_TAUNT\"", "\"three\"", "\"IDS_TAUNT\"", "\"four\"",
            "\"IDS_OTHER\"", "\"x\"", "\"IDS_LAST\"", "\"y\""], ids);
        Assert.Equal(0, Core.Validation.StringFixes.CountScattered(after.Index.Strings));
        Assert.Equal(6, after.Index.Strings.TotalCount);
    }
}
