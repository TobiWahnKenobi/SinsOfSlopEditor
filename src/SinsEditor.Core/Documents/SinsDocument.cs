using System.Text;

namespace SinsEditor.Core.Documents;

/// <summary>Text encoding of a file as found on disk, so it can be written back unchanged.</summary>
public enum SinsTextEncoding
{
    Utf8,
    Utf8WithBom,
    /// <summary>Fallback for files that are not valid UTF-8, read as Windows-1252 (every byte round-trips).</summary>
    Ansi,
}

/// <summary>
/// A Sins text file (<c>.entity</c>, <c>.str</c>, <c>.brushes</c>, <c>.manifest</c>, <c>.mesh</c>, …) as an ordered list of lines.
/// Saving a document that was not edited reproduces the original bytes exactly.
/// </summary>
public sealed class SinsDocument
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding Windows1252 = CreateWindows1252();

    private static Encoding CreateWindows1252()
    {
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return System.Text.Encoding.GetEncoding(1252);
    }

    private SinsDocument(List<SinsLine> lines, SinsTextEncoding encoding, string defaultEnding)
    {
        Lines = lines;
        Encoding = encoding;
        DefaultEnding = defaultEnding;
    }

    /// <summary>All lines, including the <c>TXT</c> header as line 0.</summary>
    public List<SinsLine> Lines { get; }

    public SinsTextEncoding Encoding { get; private set; }

    /// <summary>Line ending used for newly inserted lines (the most common one in the file).</summary>
    public string DefaultEnding { get; private set; }

    public bool HasNonAsciiText => Lines.Any(l => l.Text.Any(c => c > 127));

    /// <summary>The format marker on the first line, e.g. <c>TXT</c> or <c>TXT2</c>.</summary>
    public string Header => Lines.Count > 0 ? Lines[0].Key : "";

    public static SinsDocument Load(string path) => Parse(File.ReadAllBytes(path));

    public static SinsDocument Parse(byte[] bytes)
    {
        SinsTextEncoding encoding;
        string text;
        if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            encoding = SinsTextEncoding.Utf8WithBom;
            text = StrictUtf8.GetString(bytes, 3, bytes.Length - 3);
        }
        else
        {
            try
            {
                text = StrictUtf8.GetString(bytes);
                encoding = SinsTextEncoding.Utf8;
            }
            catch (DecoderFallbackException)
            {
                text = Windows1252.GetString(bytes);
                encoding = SinsTextEncoding.Ansi;
            }
        }

        var lines = new List<SinsLine>();
        int crlf = 0, lf = 0;
        int start = 0;
        while (start < text.Length)
        {
            int i = text.IndexOfAny(['\r', '\n'], start);
            if (i < 0)
            {
                lines.Add(SinsLine.Parse(text[start..], ""));
                break;
            }
            string ending;
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') { ending = "\r\n"; crlf++; }
            else if (text[i] == '\n') { ending = "\n"; lf++; }
            else ending = "\r";
            lines.Add(SinsLine.Parse(text[start..i], ending));
            start = i + ending.Length;
        }

        return new SinsDocument(lines, encoding, crlf > lf ? "\r\n" : "\n");
    }

    public byte[] ToBytes()
    {
        var sb = new StringBuilder();
        foreach (var line in Lines)
        {
            sb.Append(line.Text).Append(line.Ending);
        }
        string text = sb.ToString();
        return Encoding switch
        {
            SinsTextEncoding.Utf8WithBom => [0xEF, 0xBB, 0xBF, .. StrictUtf8.GetBytes(text)],
            SinsTextEncoding.Ansi => Windows1252.GetBytes(text),
            _ => StrictUtf8.GetBytes(text),
        };
    }

    public void Save(string path) => File.WriteAllBytes(path, ToBytes());

    /// <summary>
    /// Converts encoding and line endings to <paramref name="convention"/>. Whether the file ends with a newline is kept.
    /// Returns true if anything changed.
    /// </summary>
    public bool ApplyConvention(TextConvention convention)
    {
        bool changed = Encoding != convention.Encoding;
        Encoding = convention.Encoding;
        DefaultEnding = convention.LineEnding;
        foreach (var line in Lines)
        {
            if (line.Ending.Length > 0 && line.Ending != convention.LineEnding)
            {
                line.Ending = convention.LineEnding;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// Inserts a line with the document's default ending. Appending after a last line that has no
    /// ending moves the "no final newline" to the new last line.
    /// </summary>
    public SinsLine InsertLine(int index, string indent, string key, string value = "")
    {
        string ending = DefaultEnding;
        if (index == Lines.Count && index > 0 && Lines[index - 1].Ending.Length == 0)
        {
            Lines[index - 1].Ending = DefaultEnding;
            ending = "";
        }
        var line = SinsLine.Create(indent, key, value, ending);
        Lines.Insert(index, line);
        return line;
    }

    public void RemoveLine(int index)
    {
        bool wasLast = index == Lines.Count - 1;
        string ending = Lines[index].Ending;
        Lines.RemoveAt(index);
        // Keep "no final newline" files that way when their last line is removed.
        if (wasLast && index > 0 && ending.Length == 0) Lines[index - 1].Ending = "";
    }

    /// <summary>Builds a tree from indentation. Tolerates irregular indentation: a line's parent is the nearest previous line with smaller depth.</summary>
    public SinsNode BuildTree()
    {
        var root = new SinsNode(null, -1, null);
        var stack = new List<SinsNode> { root };
        for (int i = 1; i < Lines.Count; i++)
        {
            var line = Lines[i];
            if (line.IsBlank) continue;
            int depth = line.Depth;
            while (stack.Count > 1 && stack[^1].Depth >= depth) stack.RemoveAt(stack.Count - 1);
            var node = new SinsNode(line, depth, stack[^1]) { LineIndex = i };
            stack[^1].Children.Add(node);
            stack.Add(node);
        }
        return root;
    }
}
