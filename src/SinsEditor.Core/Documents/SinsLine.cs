namespace SinsEditor.Core.Documents;

/// <summary>
/// One line of a Sins text file, split into indent, key, separator, value and trailing whitespace.
/// Concatenating the parts plus <see cref="Ending"/> reproduces the original bytes exactly.
/// </summary>
public sealed class SinsLine
{
    public SinsLine(string indent, string key, string separator, string value, string trailing, string ending)
    {
        Indent = indent;
        Key = key;
        Separator = separator;
        Value = value;
        Trailing = trailing;
        Ending = ending;
    }

    public string Indent { get; private set; }
    public string Key { get; }
    /// <summary>Whitespace between key and value (usually a single space).</summary>
    public string Separator { get; private set; }
    /// <summary>Raw value text, without surrounding whitespace. Empty for section headers.</summary>
    public string Value { get; private set; }
    public string Trailing { get; }
    /// <summary>"\r\n", "\n", "\r", or "" for the last line of a file without final newline.</summary>
    public string Ending { get; internal set; }

    public bool IsBlank => Key.Length == 0;
    public bool HasValue => Value.Length > 0;

    /// <summary>Nesting level derived from the indentation (tab = 1, four spaces = 1).</summary>
    public int Depth
    {
        get
        {
            int tabs = 0, spaces = 0;
            foreach (char c in Indent)
            {
                if (c == '\t') tabs++; else spaces++;
            }
            return tabs + spaces / 4;
        }
    }

    public string Text => Indent + Key + Separator + Value + Trailing;

    public SinsValueKind ValueKind => SinsValue.Classify(Value);

    /// <summary>Replaces the raw value text, keeping indentation and trailing whitespace.</summary>
    public void SetRawValue(string value)
    {
        if (value.Length > 0 && Separator.Length == 0) Separator = " ";
        Value = value;
    }

    public void SetString(string value) => SetRawValue(SinsValue.FormatString(value));

    public static SinsLine Parse(string text, string ending)
    {
        int i = 0;
        while (i < text.Length && (text[i] == '\t' || text[i] == ' ')) i++;
        string indent = text[..i];

        int end = text.Length;
        while (end > i && (text[end - 1] == '\t' || text[end - 1] == ' ')) end--;
        string trailing = text[end..];

        int keyEnd = i;
        while (keyEnd < end && text[keyEnd] != ' ' && text[keyEnd] != '\t') keyEnd++;
        string key = text[i..keyEnd];

        int valueStart = keyEnd;
        while (valueStart < end && (text[valueStart] == ' ' || text[valueStart] == '\t')) valueStart++;

        // A key with only whitespace after it keeps that whitespace as trailing, not as separator.
        if (valueStart >= end)
            return new SinsLine(indent, key, "", "", text[keyEnd..], ending);

        return new SinsLine(indent, key, text[keyEnd..valueStart], text[valueStart..end], trailing, ending);
    }

    public static SinsLine Create(string indent, string key, string value, string ending) =>
        new(indent, key, value.Length > 0 ? " " : "", value, "", ending);

    public override string ToString() => Text;
}
