using System.Text;
using SinsEditor.Core;
using SinsEditor.Core.Documents;
using SinsEditor.Core.Editing;
using SinsEditor.Core.FileSystem;
using SinsEditor.Core.Index;
using SinsEditor.Core.Research;
using SinsEditor.Tests.Index;
using Xunit.Abstractions;

namespace SinsEditor.Tests.Research;

public sealed class ResearchWorkspaceFixture : IAsyncLifetime
{
    public ModWorkspace? Workspace { get; private set; }

    public async Task InitializeAsync()
    {
        if (TestPaths.GameDir is not { } game || TestPaths.TestMod is not { } mod) return;
        Workspace = await ModWorkspace.OpenAsync(new WorkspaceOptions(game, mod, [], "English"),
            cacheRoot: Path.Combine(Path.GetTempPath(), "SinsEditorTests", "cache"));
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

public class ResearchTreeTests(ResearchWorkspaceFixture fixture, ITestOutputHelper output) : IClassFixture<ResearchWorkspaceFixture>
{
    private ResearchTree Centauri()
    {
        Skip.If(fixture.Workspace is null, "Game directory or test mod not configured.");
        return ResearchTree.Build(fixture.Workspace!, fixture.Workspace!.Index.Entities["PlayerCentauri"]);
    }

    [SkippableFact]
    public void Reads_layout_blocks_and_subjects()
    {
        var tree = Centauri();

        Assert.Equal(new PixelPoint(184, 8), tree.Layouts["Combat"].ButtonOffset);
        Assert.Equal(new PixelPoint(50, 36), tree.Layouts["Combat"].ButtonSize);
        Assert.Equal(2, tree.BlocksOf("Combat").Count());
        Assert.Equal(new PixelRect(50, 345, 690, 155), tree.BlocksOf("Combat").Single(b => b.Index == 1).Area);
        Assert.Equal(135, tree.Subjects.Count + tree.MissingSubjects.Count);
        Assert.Equal("ResearchWindowArrowRight1", tree.ArrowBrushes[ArrowShape.Right1]);
    }

    [SkippableFact]
    public void Places_centurion_and_links_its_prerequisite()
    {
        var tree = Centauri();
        var centurion = tree.Subject("ResearchSubject_Centauri_Centurion_Attack_Cruiser")!;

        Assert.Equal(("Combat", 1, 4, 0), (centurion.Field, centurion.Block, centurion.Column, centurion.Row));
        var cell = tree.CellOf(centurion)!.Value;
        // block area x 50 + offset 184 + 4 × (50 + 14); y 345 + 8.
        Assert.Equal(new PixelPoint(50 + 184 + 4 * 64, 345 + 8), cell.Origin);
        var link = Assert.Single(tree.Links, l => l.To == centurion);
        Assert.Equal("ResearchSubject_Centauri_League_Reprisal", link.From.Name);
    }

    [SkippableFact]
    public void Reports_overview()
    {
        var tree = Centauri();
        output.WriteLine($"subjects {tree.Subjects.Count}, links {tree.Links.Count}, unplaced {tree.Unplaced.Count()}, missing {tree.MissingSubjects.Count}");
        foreach (var g in tree.Links.GroupBy(l => l.Shape).OrderByDescending(g => g.Count())) output.WriteLine($"  {g.Key}: {g.Count()}");
        foreach (var issue in tree.Issues.Take(30)) output.WriteLine($"  {issue.Severity}: {issue.Message}");
    }

    [Theory]
    [InlineData(1, 0, ArrowShape.Right1)]
    [InlineData(3, 0, ArrowShape.Right3)]
    [InlineData(1, 1, ArrowShape.Right1Down1)]
    [InlineData(0, 1, ArrowShape.Down1)]
    [InlineData(0, -2, ArrowShape.Up2)]
    [InlineData(2, -1, ArrowShape.Up1Right2)]
    [InlineData(-1, 0, ArrowShape.Unsupported)]
    [InlineData(5, 0, ArrowShape.Unsupported)]
    public void Classifies_arrow_shapes(int dx, int dy, ArrowShape expected)
    {
        var from = Subject("A", "Combat", 0, 2, 2);
        var to = Subject("B", "Combat", 0, 2 + dx, 2 + dy);

        Assert.Equal(expected, ResearchTree.Classify(from, to));
    }

    [Fact]
    public void Classifies_links_across_blocks_and_fields()
    {
        Assert.Equal(ArrowShape.BlockDown1, ResearchTree.Classify(Subject("A", "Combat", 0, 3, 4), Subject("B", "Combat", 1, 3, 0)));
        Assert.Equal(ArrowShape.Right1BlockDown1, ResearchTree.Classify(Subject("A", "Combat", 0, 3, 4), Subject("B", "Combat", 1, 4, 0)));
        Assert.Equal(ArrowShape.BlockUp1, ResearchTree.Classify(Subject("A", "Combat", 1, 3, 0), Subject("B", "Combat", 0, 3, 4)));
        Assert.Equal(ArrowShape.OtherTab, ResearchTree.Classify(Subject("A", "Defense", 0, 0, 0), Subject("B", "Combat", 0, 1, 0)));
    }

    private static ResearchSubject Subject(string name, string field, int block, int col, int row)
    {
        var file = new ResolvedFile($"GameInfo/{name}.entity", new Layer("Mod", ".", LayerKind.EditedMod), "x", []);
        var doc = SinsDocument.Parse("TXT\n"u8.ToArray());
        return new ResearchSubject(new EntityRecord(name, "ResearchSubject", file, doc, true), doc, field, block, col, row, col, 1, null, []);
    }
}

public class ResearchEditTests
{
    private const string Subject =
        "TXT\r\n" +
        "entityType \"ResearchSubject\"\r\n" +
        "researchWindowLocation\r\n" +
        "\tblock 1\r\n" +
        "\tpos [ 4 , 0 ]\r\n" +
        "ResearchField \"Combat\"\r\n" +
        "Prerequisites\r\n" +
        "\tNumResearchPrerequisites 1\r\n" +
        "\tResearchPrerequisite\r\n" +
        "\t\tSubject \"A\"\r\n" +
        "\t\tLevel 1\r\n" +
        "\tRequiredFactionNameID \"\"\r\n" +
        "\tRequiredCompletedResearchSubjects 0\r\n" +
        "Tier 4\r\n";

    private static EntityEditSession Session(string text = Subject)
    {
        var file = new ResolvedFile("GameInfo/X.entity", new Layer("Mod", ".", LayerKind.EditedMod), "X.entity", []);
        return new EntityEditSession(new EntityRecord("X", "ResearchSubject", file, SinsDocument.Parse(Encoding.UTF8.GetBytes(text)), true));
    }

    [Fact]
    public void Move_changes_field_block_position_and_tier_in_one_step()
    {
        var s = Session();

        ResearchEdits.Move(s, "Defense", 0, 2, 3);

        Assert.Contains("ResearchField \"Defense\"\r\n", s.Text);
        Assert.Contains("\tblock 0\r\n", s.Text);
        Assert.Contains("\tpos [ 2 , 3 ]\r\n", s.Text);
        Assert.Contains("Tier 2\r\n", s.Text);
        s.Undo();
        Assert.Equal(Subject, s.Text);
    }

    [Fact]
    public void Move_leaves_a_tier_that_differs_from_the_column()
    {
        var s = Session(Subject.Replace("Tier 4", "Tier 6"));

        ResearchEdits.Move(s, "Combat", 1, 2, 0);

        Assert.Contains("Tier 6\r\n", s.Text);
    }

    [Fact]
    public void Add_and_remove_prerequisites_keep_the_count()
    {
        var s = Session();

        Assert.True(ResearchEdits.AddPrerequisite(s, "B", 2));
        Assert.False(ResearchEdits.AddPrerequisite(s, "B"));
        Assert.Contains("\tNumResearchPrerequisites 2\r\n", s.Text);
        Assert.Contains("\tResearchPrerequisite\r\n\t\tSubject \"B\"\r\n\t\tLevel 2\r\n\tRequiredFactionNameID", s.Text);

        Assert.True(ResearchEdits.RemovePrerequisite(s, "A"));
        Assert.True(ResearchEdits.RemovePrerequisite(s, "B"));
        Assert.Contains("\tNumResearchPrerequisites 0\r\n\tRequiredFactionNameID", s.Text);

        Assert.True(ResearchEdits.AddPrerequisite(s, "C"));
        Assert.Contains("\tNumResearchPrerequisites 1\r\n\tResearchPrerequisite\r\n\t\tSubject \"C\"\r\n\t\tLevel 1\r\n", s.Text);
    }
}
