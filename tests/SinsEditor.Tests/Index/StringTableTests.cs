using System.Text;
using SinsEditor.Core;
using SinsEditor.Core.Documents;
using SinsEditor.Core.FileSystem;
using SinsEditor.Core.Index;
using SinsEditor.Core.Validation;

namespace SinsEditor.Tests.Index;

public sealed class StringTableTests : IAsyncLifetime
{
    private static readonly string Strings =
        "TXT\r\n" +
        "NumStrings 8\r\n" +
        Block("IDS_A", "Alpha") +
        // Taunt-style variants: consecutive, different text.
        Block("IDS_TAUNT_X", "One") +
        Block("IDS_TAUNT_X", "Two") +
        Block("IDS_TAUNT_X", "Three") +
        // An identical copy, separated from the original.
        Block("IDS_B", "Beta") +
        Block("IDS_C", "Gamma") +
        Block("IDS_B", "Beta") +
        // Defined again elsewhere with different text.
        Block("IDS_A", "Other alpha");

    private static string Block(string id, string value) => $"StringInfo\r\n\tID \"{id}\"\r\n\tValue \"{value}\"\r\n";

    private readonly string _mod = Path.Combine(Path.GetTempPath(), "SinsEditorTests", "StringMod-" + Guid.NewGuid().ToString("N"));
    private ModWorkspace? _workspace;

    public async Task InitializeAsync()
    {
        if (TestPaths.GameDir is not { } game) return;
        Directory.CreateDirectory(Path.Combine(_mod, "String"));
        File.Copy(Path.Combine(game, "entity.manifest"), Path.Combine(_mod, "Entity.manifest"));
        // Written with LF on purpose: the fix must also convert line endings.
        await File.WriteAllTextAsync(Path.Combine(_mod, "String", "English.str"), Strings.Replace("\r\n", "\n"));
        _workspace = await ModWorkspace.OpenAsync(new WorkspaceOptions(game, _mod, [], "English"),
            cacheRoot: Path.Combine(Path.GetTempPath(), "SinsEditorTests", "cache"));
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_mod)) Directory.Delete(_mod, recursive: true);
        return Task.CompletedTask;
    }

    private static StringTable Table() =>
        StringTable.Load("English", new ResolvedFile("String/English.str", new Layer("Mod", ".", LayerKind.EditedMod), "x", []),
            SinsDocument.Parse(Encoding.UTF8.GetBytes(Strings)));

    [Fact]
    public void Classifies_variants_copies_and_scattered_definitions()
    {
        var kinds = Table().Duplicates.ToDictionary(d => d.Id, d => d.Kind);

        Assert.Equal(DuplicateKind.Variants, kinds["IDS_TAUNT_X"]);
        Assert.Equal(DuplicateKind.IdenticalCopies, kinds["IDS_B"]);
        Assert.Equal(DuplicateKind.Scattered, kinds["IDS_A"]);
    }

    [Fact]
    public void Counts_every_block_and_returns_the_first_variant()
    {
        var table = Table();

        Assert.Equal(8, table.TotalCount);
        Assert.Equal("One", table.Get("IDS_TAUNT_X"));
        Assert.Equal(["One", "Two", "Three"], table.AllOf("ids_taunt_x").Select(e => e.Value));
        Assert.Equal(1, StringFixes.CountIdenticalCopies(table));
    }

    [SkippableFact]
    public void Variants_are_not_problems_but_copies_and_scattered_are()
    {
        Skip.If(_workspace is null, "Game directory not configured.");
        var problems = _workspace!.Problems.Where(p => p.RelativePath == "String/English.str").ToList();

        Assert.Single(problems, p => p.Code == ProblemCodes.DuplicateString && p.Message.Contains("IDS_B"));
        Assert.Single(problems, p => p.Code == ProblemCodes.ScatteredString && p.Message.Contains("IDS_A"));
        Assert.DoesNotContain(problems, p => p.Message.Contains("IDS_TAUNT_X"));
        Assert.DoesNotContain(problems, p => p.Code == ProblemCodes.StringCountMismatch);
    }

    [SkippableFact]
    public void Fix_removes_identical_copies_and_updates_the_count()
    {
        Skip.If(_workspace is null, "Game directory not configured.");

        int removed = StringFixes.RemoveIdenticalCopies(_workspace!);

        Assert.Equal(1, removed);
        string text = File.ReadAllText(Path.Combine(_mod, "String", "English.str"));
        Assert.Contains("NumStrings 7\r\n", text);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "IDS_B"));
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(text, "IDS_TAUNT_X").Count);
        Assert.Contains("Other alpha", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
    }
}
