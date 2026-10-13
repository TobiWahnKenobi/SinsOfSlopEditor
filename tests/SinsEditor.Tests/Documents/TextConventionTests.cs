using SinsEditor.Core.Documents;

namespace SinsEditor.Tests.Documents;

public class TextConventionTests
{
    [Theory]
    // Same file exists in the base game: follow it.
    [InlineData("Window/LoadScreen.window", SinsTextEncoding.Utf8, false, SinsTextEncoding.Utf8)]
    [InlineData("Galaxy/Foo.galaxy", SinsTextEncoding.Utf8WithBom, false, SinsTextEncoding.Utf8WithBom)]
    // Base game file is binary: callers pass UTF-8 with BOM, ConvertData's text output.
    [InlineData("GameInfo/Frigate_X.entity", SinsTextEncoding.Utf8WithBom, false, SinsTextEncoding.Utf8WithBom)]
    // New files.
    [InlineData("GameInfo/New.entity", null, false, SinsTextEncoding.Utf8WithBom)]
    [InlineData("Mesh/New.mesh", null, false, SinsTextEncoding.Utf8WithBom)]
    [InlineData("Window/New.window", null, false, SinsTextEncoding.Utf8)]
    // Non-ASCII content always gets a BOM.
    [InlineData("String/English.str", SinsTextEncoding.Utf8, true, SinsTextEncoding.Utf8WithBom)]
    [InlineData("Window/New.window", null, true, SinsTextEncoding.Utf8WithBom)]
    // Never writes ANSI, even if the game had it.
    [InlineData("String/Old.str", SinsTextEncoding.Ansi, true, SinsTextEncoding.Utf8WithBom)]
    public void Resolve_picks_encoding(string path, SinsTextEncoding? gameEncoding, bool nonAscii, SinsTextEncoding expected)
    {
        var convention = TextConventions.Resolve(path, gameEncoding, nonAscii);

        Assert.Equal(expected, convention.Encoding);
        Assert.Equal("\r\n", convention.LineEnding);
    }
}
