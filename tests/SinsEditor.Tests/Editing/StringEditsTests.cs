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
}
