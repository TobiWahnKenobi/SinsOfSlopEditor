using SinsEditor.Core;
using SinsEditor.Core.Validation;
using Xunit.Abstractions;

namespace SinsEditor.Tests.Validation;

public sealed class AiReadinessTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly string _mod = Path.Combine(Path.GetTempPath(), "SinsEditorTests", "AiMod-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(_mod)) Directory.Delete(_mod, recursive: true);
        return Task.CompletedTask;
    }

    private Task<ModWorkspace> Open(IReadOnlyList<string> baseMods) => ModWorkspace.OpenAsync(new WorkspaceOptions(TestPaths.GameDir!, _mod, baseMods, "English"),
        cacheRoot: Path.Combine(Path.GetTempPath(), "SinsEditorTests", "cache"));

    [SkippableFact]
    public async Task The_base_games_races_have_what_the_AI_needs()
    {
        Skip.If(TestPaths.GameDir is null, "Game directory not configured.");
        Directory.CreateDirectory(_mod);
        var ws = await Open([]);
        var problems = AiReadinessCheck.Check(ws.Index).ToList();
        foreach (var p in problems) output.WriteLine(p.Message);
        Assert.Empty(problems);
    }

    [SkippableFact]
    public async Task A_race_without_a_colony_frigate_is_reported()
    {
        Skip.If(TestPaths.GameDir is null, "Game directory not configured.");
        Directory.CreateDirectory(_mod);
        var ws = await Open([]);
        // Rename every colony frigate's role in the race's copy: the AI no longer finds one.
        var race = ws.Index.Races.First(r => r.Name == "PlayerTechLoyalist");
        var colony = ws.Index.ItemsOf(race).First(i => i.Section == "frigateInfo" && i.Entity!.Document.Lines.Any(l => l.Key == "frigateRoleType" && l.Value == "\"Colony\"")).Entity!;
        var session = ws.SessionFor(colony);
        session.SetValue(session.Document.Lines.FindIndex(l => l.Key == "frigateRoleType"), "\"Light\"");
        await ws.SaveAsync(session, Core.Editing.SaveFormat.Text);

        var problems = ws.Problems.Where(p => p.Code == ProblemCodes.AiReadiness).ToList();
        foreach (var p in problems) output.WriteLine(p.Message);
        Assert.Contains(problems, p => p.Message.Contains("colony frigate") && p.RelativePath == race.File.RelativePath);
        Assert.All(problems, p => Assert.Equal(ProblemSeverity.Warning, p.Severity));
    }

    [SkippableFact]
    public async Task Test_mod_report()
    {
        Skip.If(TestPaths.GameDir is null || TestPaths.TestMod is null, "Game directory or test mod not configured.");
        Directory.CreateDirectory(_mod);
        var ws = await Open([TestPaths.TestMod!]);
        foreach (var p in AiReadinessCheck.Check(ws.Index)) output.WriteLine(p.Message);
    }
}
