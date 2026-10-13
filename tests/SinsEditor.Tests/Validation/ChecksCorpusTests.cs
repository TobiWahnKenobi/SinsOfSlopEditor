using SinsEditor.Core.Documents;
using SinsEditor.Core.Index;
using SinsEditor.Core.Validation;
using SinsEditor.Tests.Index;
using Xunit.Abstractions;

namespace SinsEditor.Tests.Validation;

/// <summary>The extra checks on the real test mod: the report shows what they find, the assertions guard against noise.</summary>
public class ChecksCorpusTests(TestModIndexFixture fixture, ITestOutputHelper output) : IClassFixture<TestModIndexFixture>
{
    [SkippableFact]
    public void Reports_check_results()
    {
        Skip.If(fixture.Index is null, "Game directory or test mod not configured.");
        var checks = Checks.Run(fixture.Index!);
        var lines = new List<string>();
        foreach (var g in checks.Problems.GroupBy(p => p.Code).OrderBy(g => g.Key))
        {
            lines.Add($"{g.Key}: {g.Count()}");
            foreach (var p in g) lines.Add($"   {p.RelativePath}:{p.LineIndex + 1}  {p.Message}");
        }
        foreach (var l in lines) output.WriteLine(l);
        if (Environment.GetEnvironmentVariable("SINSEDITOR_REPORT") is { } file) File.WriteAllLines(file, lines);
    }

    [SkippableTheory]
    [InlineData("CapitalShip", "NumWeapons", true)]
    [InlineData("Buff", "numEntityModifiers", true)]
    [InlineData("Buff", "numFinishConditions", true)]
    [InlineData("CapitalShip", "TargetCountPerBank:LEFT", false)]
    [InlineData("CapitalShip", "MinimumArtifactLevel", false)]
    public void Learns_counters_from_the_base_game(string type, string key, bool counter)
    {
        Skip.If(fixture.Index is null, "Game directory or test mod not configured.");
        Assert.Equal(counter, EntitySchema.Learn(fixture.Index!).IsCounter(type, key));
    }

    [SkippableFact]
    public void Base_game_entities_pass_their_own_schema()
    {
        Skip.If(fixture.Index is null, "Game directory or test mod not configured.");
        var schema = EntitySchema.Learn(fixture.Index!);
        var errors = fixture.Index!.Entities.Values.Where(e => !e.File.Layer.IsEditable)
            .SelectMany(schema.Check).Where(p => p.Severity != ProblemSeverity.Info).ToList();
        foreach (var p in errors.Take(20)) output.WriteLine($"{p.RelativePath}:{p.LineIndex + 1} {p.Message}");
        Assert.Empty(errors);
    }

    [SkippableFact]
    public void Finds_broken_counts_and_values()
    {
        Skip.If(fixture.Index is null, "Game directory or test mod not configured.");
        var index = fixture.Index!;
        var schema = EntitySchema.Learn(index);
        var ship = index.Entities.Values.First(e => !e.File.Layer.IsEditable && e.EntityType == "CapitalShip"
            && e.Document.Lines.Any(l => l.Key == "NumWeapons" && l.Value != "0" && l.Value != "1"));
        SinsDocument Copy() => SinsDocument.Parse(ship.Document.ToBytes());
        EntityRecord With(SinsDocument doc) => ship with { Document = doc };

        Assert.DoesNotContain(schema.Check(With(Copy())), p => p.Severity != ProblemSeverity.Info);

        var counted = Copy();
        var weapons = counted.Lines.First(l => l.Key == "NumWeapons");
        weapons.SetRawValue((int.Parse(weapons.Value) + 1).ToString());
        Assert.Contains(schema.Check(With(counted)), p => p.Code == ProblemCodes.CountMismatch);

        var badBool = Copy();
        badBool.Lines.First(l => l.Value is "TRUE" or "FALSE").SetRawValue("\"TRUE\"");
        var badFloat = Copy();
        badFloat.Lines.First(l => l.ValueKind == SinsValueKind.Float).SetRawValue("0,500000");
        Assert.Contains(schema.Check(With(badBool)), p => p.Code == ProblemCodes.WrongValueKind);
        Assert.Contains(schema.Check(With(badFloat)), p => p.Code == ProblemCodes.WrongValueKind);
    }
}
