using SinsEditor.Core;
using SinsEditor.Core.Documents;
using SinsEditor.Core.Validation;

namespace SinsEditor.Tests.Validation;

public sealed class ValueFixesTests : IAsyncLifetime
{
    private readonly string _mod = Path.Combine(Path.GetTempPath(), "SinsEditorTests", "ValueMod-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("\"TRUE\"", SinsValueKind.Bool, "TRUE")]
    [InlineData("FALSE\"", SinsValueKind.Bool, "FALSE")]
    [InlineData("\"false\"", SinsValueKind.Bool, "FALSE")]
    [InlineData("0,500000", SinsValueKind.Float, "0.500000")]
    [InlineData("-12,5", SinsValueKind.Float, "-12.5")]
    [InlineData("0. 05000", SinsValueKind.Float, null)]
    [InlineData("\"yes\"", SinsValueKind.Bool, null)]
    [InlineData("2.5", SinsValueKind.Integer, null)]
    public void Fixes_only_unambiguous_values(string raw, SinsValueKind expected, string? fixedValue) =>
        Assert.Equal(fixedValue, ValueFixes.Fix(raw, expected));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(_mod)) Directory.Delete(_mod, recursive: true);
        return Task.CompletedTask;
    }

    private Task<ModWorkspace> Open() => ModWorkspace.OpenAsync(new WorkspaceOptions(TestPaths.GameDir!, _mod, [], "English"),
        cacheRoot: Path.Combine(Path.GetTempPath(), "SinsEditorTests", "cache"));

    [SkippableFact]
    public async Task Fix_all_repairs_the_mods_entities()
    {
        Skip.If(TestPaths.GameDir is null, "Game directory not configured.");
        Directory.CreateDirectory(Path.Combine(_mod, "GameInfo"));
        File.Copy(Path.Combine(TestPaths.GameDir!, "entity.manifest"), Path.Combine(_mod, "entity.manifest"));
        var ws = await Open();

        // A game entity overridden by the mod with a quoted boolean and a decimal comma.
        var entity = ws.Index.Entities.Values.First(e => e.EntityType == "Frigate"
            && e.Document.Lines.Any(l => l.Value is "TRUE" or "FALSE") && e.Document.Lines.Any(l => l.ValueKind == SinsValueKind.Float));
        var doc = SinsDocument.Parse(entity.Document.ToBytes());
        int boolLine = doc.Lines.FindIndex(l => l.Value is "TRUE" or "FALSE"), floatLine = doc.Lines.FindIndex(l => l.ValueKind == SinsValueKind.Float);
        string boolValue = doc.Lines[boolLine].Value, floatValue = doc.Lines[floatLine].Value;
        doc.Lines[boolLine].SetRawValue($"\"{boolValue}\"");
        doc.Lines[floatLine].SetRawValue(floatValue.Replace('.', ','));
        doc.Save(Path.Combine(_mod, "GameInfo", entity.Name + ".entity"));

        ws = await Open();
        Assert.Equal(2, ws.Problems.Count(p => p.Code == ProblemCodes.WrongValueKind));
        Assert.Equal(2, ValueFixes.Fixable(ws).Count);

        var (done, skipped) = await ValueFixes.FixAllAsync(ws);
        Assert.Equal((2, 0), (done, skipped));
        Assert.DoesNotContain(ws.Problems, p => p.Code == ProblemCodes.WrongValueKind);

        var after = await Open();
        Assert.DoesNotContain(after.Problems, p => p.Code == ProblemCodes.WrongValueKind);
        var saved = after.Index.Entities[entity.Name].Document;
        Assert.Equal(boolValue, saved.Lines[boolLine].Value);
        Assert.Equal(floatValue, saved.Lines[floatLine].Value);
    }
}
