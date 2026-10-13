using SinsEditor.Core;
using SinsEditor.Core.Index;
using SinsEditor.Core.Validation;
using SinsEditor.Tests.Research;
using Xunit.Abstractions;

namespace SinsEditor.Tests.Index;

/// <summary>Importing strings the mod uses but lacks from lower layers, on a throwaway mod with an almost empty string table.</summary>
public sealed class ImportStringsTests : IAsyncLifetime
{
    private readonly string _mod = Path.Combine(Path.GetTempPath(), "SinsEditorTests", "StringImport-" + Guid.NewGuid().ToString("N"));
    private ModWorkspace? _workspace;

    public async Task InitializeAsync()
    {
        if (TestPaths.GameDir is not { } game) return;
        Directory.CreateDirectory(Path.Combine(_mod, "String"));
        File.Copy(Path.Combine(game, "entity.manifest"), Path.Combine(_mod, "Entity.manifest"));
        // The mod's table replaces the game's, so every string the game's entities use is now missing.
        await File.WriteAllTextAsync(Path.Combine(_mod, "String", "English.str"),
            "TXT\r\nNumStrings 1\r\nStringInfo\r\n\tID \"IDS_MY_OWN\"\r\n\tValue \"Mine\"\r\n");
        _workspace = await Open();
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_mod)) Directory.Delete(_mod, recursive: true);
        return Task.CompletedTask;
    }

    private Task<ModWorkspace> Open() => ModWorkspace.OpenAsync(new WorkspaceOptions(TestPaths.GameDir!, _mod, [], "English"),
        cacheRoot: Path.Combine(Path.GetTempPath(), "SinsEditorTests", "cache"));

    [SkippableFact]
    public async Task Imports_missing_strings_from_the_base_game()
    {
        Skip.If(_workspace is null, "Game directory not configured.");
        var ws = _workspace!;
        int missingBefore = ws.Problems.Count(p => p.Code == ProblemCodes.MissingString);
        Assert.True(missingBefore > 100);

        var importable = StringFixes.FindImportable(ws);
        Assert.NotEmpty(importable);
        Assert.All(importable, s => Assert.Equal("Base game", s.LayerName));

        int added = StringFixes.ImportStrings(ws, importable);
        Assert.True(added >= importable.Count);

        var after = await Open();
        int missingAfter = after.Problems.Count(p => p.Code == ProblemCodes.MissingString);
        Assert.True(missingAfter < missingBefore);
        Assert.Empty(StringFixes.FindImportable(after));
        Assert.Equal("Mine", after.Index.Strings.Get("IDS_MY_OWN"));
        Assert.DoesNotContain(after.Problems, p => p.Code == ProblemCodes.StringCountMismatch);
        Assert.Equal(1 + added, after.Index.Strings.TotalCount);
        // Imported text is the game's text.
        var sample = importable[0];
        Assert.Equal(sample.Values[0], after.Index.Strings.Get(sample.Id));
        string text = await File.ReadAllTextAsync(Path.Combine(_mod, "String", "English.str"));
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
    }
}

/// <summary>Read-only: how many of the test mod's missing strings the base game could supply.</summary>
public class ImportStringsReport(ResearchWorkspaceFixture fixture, ITestOutputHelper output) : IClassFixture<ResearchWorkspaceFixture>
{
    [SkippableFact]
    public void Test_mod_has_importable_strings()
    {
        Skip.If(fixture.Workspace is null, "Not configured.");
        var ws = fixture.Workspace!;
        int missing = ws.Index.References.Where(r => r.Kind == ReferenceKind.String && !r.Resolved).Select(r => r.Target).Distinct().Count();
        var importable = StringFixes.FindImportable(ws);
        output.WriteLine($"missing string IDs {missing}, importable {importable.Count}");
        foreach (var s in importable.Take(5)) output.WriteLine($"  {s.Id} = {s.Values[0]}");
        Assert.True(importable.Count <= missing);
    }
}
