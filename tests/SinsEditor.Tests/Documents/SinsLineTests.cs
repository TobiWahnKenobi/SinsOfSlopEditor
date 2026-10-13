using SinsEditor.Core.Documents;

namespace SinsEditor.Tests.Documents;

public class SinsLineTests
{
    [Theory]
    [InlineData("entityType \"Frigate\"", "", "entityType", "\"Frigate\"", "", SinsValueKind.String)]
    [InlineData("\t\t\tcount 9", "\t\t\t", "count", "9", "", SinsValueKind.Integer)]
    [InlineData("\tPreBuffCooldownTime 11.000000", "\t", "PreBuffCooldownTime", "11.000000", "", SinsValueKind.Float)]
    [InlineData("canColonize TRUE", "", "canColonize", "TRUE", "", SinsValueKind.Bool)]
    [InlineData("\t\t\tarea [ 50 , 93 , 690 , 242]", "\t\t\t", "area", "[ 50 , 93 , 690 , 242]", "", SinsValueKind.Vector)]
    [InlineData("\t\tPage:0", "\t\t", "Page:0", "", "", SinsValueKind.None)]
    [InlineData("planetArtifactDensity .15", "", "planetArtifactDensity", ".15", "", SinsValueKind.Float)]
    [InlineData("travelGridColor:Planet 6fffffff\t", "", "travelGridColor:Planet", "6fffffff", "\t", SinsValueKind.Other)]
    [InlineData("\t\tweaponDefName = \"Weapon\"", "\t\t", "weaponDefName", "= \"Weapon\"", "", SinsValueKind.Other)]
    [InlineData("needsToFaceTarget ", "", "needsToFaceTarget", "", " ", SinsValueKind.None)]
    [InlineData("\t", "\t", "", "", "", SinsValueKind.None)]
    public void Parse_splits_parts(string text, string indent, string key, string value, string trailing, SinsValueKind kind)
    {
        var line = SinsLine.Parse(text, "\n");

        Assert.Equal(indent, line.Indent);
        Assert.Equal(key, line.Key);
        Assert.Equal(value, line.Value);
        Assert.Equal(trailing, line.Trailing);
        Assert.Equal(kind, line.ValueKind);
        Assert.Equal(text, line.Text);
    }

    [Fact]
    public void SetRawValue_keeps_indent_and_trailing_whitespace()
    {
        var line = SinsLine.Parse("\t\tstartingCrystal 250\t", "\r\n");

        line.SetRawValue("300");

        Assert.Equal("\t\tstartingCrystal 300\t", line.Text);
        Assert.Equal("\r\n", line.Ending);
    }

    [Fact]
    public void SetRawValue_on_bare_key_adds_separator()
    {
        var line = SinsLine.Parse("needsToFaceTarget", "\n");

        line.SetRawValue("TRUE");

        Assert.Equal("needsToFaceTarget TRUE", line.Text);
    }

    [Fact]
    public void Depth_counts_tabs_and_groups_of_four_spaces()
    {
        Assert.Equal(2, SinsLine.Parse("\t\tkey 1", "\n").Depth);
        Assert.Equal(2, SinsLine.Parse("\t    key 1", "\n").Depth);
        Assert.Equal(0, SinsLine.Parse("key 1", "\n").Depth);
    }

    [Fact]
    public void TryGetVector_parses_components()
    {
        Assert.True(SinsValue.TryGetVector("[ 4 , 0 ]", out var v));
        Assert.Equal([4.0, 0.0], v);
        Assert.True(SinsValue.TryGetVector("[0, 39]", out v));
        Assert.Equal([0.0, 39.0], v);
        Assert.False(SinsValue.TryGetVector("[ a , 0 ]", out _));
    }
}
