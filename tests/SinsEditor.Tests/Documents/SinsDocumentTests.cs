using System.Text;
using SinsEditor.Core.Documents;

namespace SinsEditor.Tests.Documents;

public class SinsDocumentTests
{
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Theory]
    [InlineData("TXT\nentityType \"Frigate\"\n")]
    [InlineData("TXT2\r\nSinsArchiveVersion 193\r\nentityType \"Player\"\r\n")]
    [InlineData("TXT\nno final newline")]
    [InlineData("TXT\r\nmixed\nendings\r\n")]
    [InlineData("TXT\n\t\n\n")]
    [InlineData("")]
    public void Round_trips_exactly(string text)
    {
        var bytes = Bytes(text);

        Assert.Equal(bytes, SinsDocument.Parse(bytes).ToBytes());
    }

    [Fact]
    public void Round_trips_utf8_bom()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Bytes("TXT\nValue \"Größe\"\n")];

        var doc = SinsDocument.Parse(bytes);

        Assert.Equal(SinsTextEncoding.Utf8WithBom, doc.Encoding);
        Assert.Equal("TXT", doc.Header);
        Assert.Equal(bytes, doc.ToBytes());
    }

    [Fact]
    public void Round_trips_invalid_utf8_as_latin1()
    {
        byte[] bytes = [.. Bytes("TXT\nValue \""), 0xE4, 0xFF, .. Bytes("\"\n")];

        var doc = SinsDocument.Parse(bytes);

        Assert.Equal(SinsTextEncoding.Latin1, doc.Encoding);
        Assert.Equal(bytes, doc.ToBytes());
    }

    [Fact]
    public void Edit_changes_only_that_line()
    {
        var doc = SinsDocument.Parse(Bytes("TXT\r\na 1\r\n\tb 2\t\r\nc 3\r\n"));

        doc.Lines[2].SetRawValue("5");

        Assert.Equal("TXT\r\na 1\r\n\tb 5\t\r\nc 3\r\n", Encoding.UTF8.GetString(doc.ToBytes()));
    }

    [Fact]
    public void InsertLine_uses_dominant_line_ending()
    {
        var doc = SinsDocument.Parse(Bytes("TXT\r\na 1\r\nb 2\n"));

        doc.InsertLine(2, "\t", "x", "\"y\"");

        Assert.Equal("TXT\r\na 1\r\n\tx \"y\"\r\nb 2\n", Encoding.UTF8.GetString(doc.ToBytes()));
    }

    [Fact]
    public void Appending_to_file_without_final_newline_keeps_it_without()
    {
        var doc = SinsDocument.Parse(Bytes("TXT\na 1"));

        doc.InsertLine(doc.Lines.Count, "", "b", "2");

        Assert.Equal("TXT\na 1\nb 2", Encoding.UTF8.GetString(doc.ToBytes()));
    }

    [Fact]
    public void Removing_last_line_keeps_missing_final_newline()
    {
        var doc = SinsDocument.Parse(Bytes("TXT\na 1\nb 2"));

        doc.RemoveLine(2);

        Assert.Equal("TXT\na 1", Encoding.UTF8.GetString(doc.ToBytes()));
    }

    [Fact]
    public void BuildTree_nests_by_indentation()
    {
        var doc = SinsDocument.Parse(Bytes(
            "TXT\nentities\n\tfrigateInfo\n\t\tPage:0\n\t\t\tcount 2\n\t\t\tentityDefName \"A\"\n\t\t\tentityDefName \"B\"\nresearch 1\n"));

        var root = doc.BuildTree();

        Assert.Equal(["entities", "research"], root.Children.Select(c => c.Key));
        var page = root.Child("entities")!.Child("frigateInfo")!.Child("Page:0")!;
        Assert.Equal(["\"A\"", "\"B\""], page.ChildrenNamed("entityDefName").Select(n => n.Value));
        Assert.Equal(4, page.Child("count")!.LineIndex);
    }

    [Fact]
    public void BuildTree_tolerates_irregular_indentation()
    {
        // Seen in ResearchScreen.window: a section at depth 0 whose children are at depth 2.
        var doc = SinsDocument.Parse(Bytes("TXT\ntabWindow\n\ta 1\nnonActive\n\t\tfontId \"x\"\n\tb 2\n"));

        var root = doc.BuildTree();

        Assert.Equal(["tabWindow", "nonActive"], root.Children.Select(c => c.Key));
        Assert.Equal(["fontId", "b"], root.Child("nonActive")!.Children.Select(c => c.Key));
    }

    [Theory]
    [InlineData("TXT\n", SinsFileFormat.Text)]
    [InlineData("TXT2\n", SinsFileFormat.Text)]
    [InlineData("BIN2....", SinsFileFormat.Binary)]
    [InlineData("hello", SinsFileFormat.Unknown)]
    public void Detects_format(string start, SinsFileFormat expected)
    {
        Assert.Equal(expected, SinsFileFormatDetector.Detect(Bytes(start)));
    }

    [Fact]
    public void Detects_text_after_bom()
    {
        Assert.Equal(SinsFileFormat.Text, SinsFileFormatDetector.Detect([0xEF, 0xBB, 0xBF, .. Bytes("TXT\n")]));
    }
}
