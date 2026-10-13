using SinsEditor.Core.Conversion;
using SinsEditor.Core.FileSystem;
using SinsEditor.Core.Index;
using Xunit.Abstractions;

namespace SinsEditor.Tests.Index;

/// <summary>Builds the index for the base game plus the test mod. Shared across tests because building takes a while.</summary>
public sealed class TestModIndexFixture : IAsyncLifetime
{
    public ModIndex? Index { get; private set; }

    public async Task InitializeAsync()
    {
        if (TestPaths.GameDir is not { } game || TestPaths.TestMod is not { } mod) return;
        var stack = new LayerStack([
            new Layer("Base game", game, LayerKind.BaseGame),
            new Layer(Path.GetFileName(mod), mod, LayerKind.EditedMod),
        ]);
        // A persistent cache under %TEMP% keeps repeated test runs fast.
        var cache = new TextCache(ConvertDataTool.FromGameDirectory(game), Path.Combine(Path.GetTempPath(), "SinsEditorTests", "cache"));
        Index = await ModIndex.BuildAsync(stack, cache, "English");
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

public class ModIndexCorpusTests(TestModIndexFixture fixture, ITestOutputHelper output) : IClassFixture<TestModIndexFixture>
{
    private ModIndex Index
    {
        get
        {
            Skip.If(fixture.Index is null, "Game directory or test mod not configured.");
            return fixture.Index!;
        }
    }

    [SkippableFact]
    public void Reports_overview()
    {
        var index = Index;
        output.WriteLine($"entities {index.Entities.Count}, manifest {index.ManifestEntries.Count}, strings {index.Strings.Entries.Count}, " +
                         $"brushes {index.Brushes.Entries.Count}, sounds {index.Sounds.Entries.Count}, meshes {index.Meshes.Count}, " +
                         $"particles {index.Particles.Count}, textures {index.Textures.Count}, references {index.References.Count}");
        foreach (var g in index.Entities.Values.GroupBy(e => e.File.Layer.Name)) output.WriteLine($"  layer {g.Key}: {g.Count()}");
        output.WriteLine("reference keys:");
        foreach (var g in index.ReferenceKeys.GroupBy(k => k.Value)) output.WriteLine($"  {g.Key,-8} {g.Count()} keys");
        output.WriteLine("problems:");
        foreach (var g in index.Problems.GroupBy(p => p.Code)) output.WriteLine($"  {g.Key}: {g.Count()}  e.g. {g.First().Message}");
        foreach (var g in index.Problems.Where(p => p.Code == "missing-reference").GroupBy(p => p.Message.Split(':')[1].Split(" points")[0].Trim()).OrderByDescending(g => g.Count()).Take(15))
        {
            output.WriteLine($"    missing via {g.Key}: {g.Count()}");
            foreach (var p in g.Take(3)) output.WriteLine($"      {p.RelativePath}: {p.Message}");
        }
    }

    [SkippableFact]
    public void Finds_all_seven_races()
    {
        var races = Index.Races.Select(r => r.Name).Order().ToList();

        Assert.Equal(["PlayerCentauri", "PlayerEA", "PlayerMinbari", "PlayerNarn", "PlayerPirate", "PlayerShadow", "PlayerVorlon"], races);
        Assert.False(Index.Exists(ReferenceKind.Entity, "PlayerPhaseRebel"));
    }

    [SkippableFact]
    public void Race_owns_its_frigates_by_page()
    {
        var centauri = Index.Entities["PlayerCentauri"];

        var corvan = Index.ItemsOf(centauri).Single(i => i.Name == "Frigate_Centauri_Corvan");

        Assert.Equal("frigateInfo", corvan.Section);
        Assert.Equal("Page:0", corvan.Page);
        Assert.NotNull(corvan.Entity);
        Assert.Equal(135, Index.ItemsOf(centauri).Count(i => i.Section == "researchInfo"));
    }

    [SkippableFact]
    public void Infers_common_reference_keys()
    {
        Assert.Equal(ReferenceKind.Entity, Index.ReferenceKeys["Page:0/entityDefName"]);
        Assert.Equal(ReferenceKind.Entity, Index.ReferenceKeys["researchInfo/entityDefName"]);
        Assert.Equal(ReferenceKind.Entity, Index.ReferenceKeys["ResearchPrerequisite/Subject"]);
        Assert.Equal(ReferenceKind.String, Index.ReferenceKeys["/NameStringID"]);
        Assert.Equal(ReferenceKind.Brush, Index.ReferenceKeys["/hudIcon"]);
        Assert.Equal(ReferenceKind.Mesh, Index.ReferenceKeys["MeshNameInfo/meshName"]);
        Assert.Equal(ReferenceKind.Sound, Index.ReferenceKeys["/SoundID"]);
        Assert.Equal(ReferenceKind.Entity, Index.ReferenceKeys["requiredShip/type"]);
    }

    [SkippableFact]
    public void Links_both_ways()
    {
        var corvan = Index.Entities["Frigate_Centauri_Corvan"];

        Assert.Contains(Index.OutgoingReferences(corvan), r => r.Kind == ReferenceKind.Mesh && r.Target == "Corvan");
        Assert.Contains(Index.IncomingReferences(ReferenceKind.Entity, "Frigate_Centauri_Corvan"), r => r.Source.Name == "PlayerCentauri");
        Assert.Equal("PlayerCentauri", Assert.Single(Index.RacesOwning("Frigate_Centauri_Corvan")).Name);
    }

    [SkippableFact]
    public void Display_names_come_from_the_string_table()
    {
        Assert.Equal("Centauri Republic", Index.DisplayName(Index.Entities["PlayerCentauri"]));
    }
}
