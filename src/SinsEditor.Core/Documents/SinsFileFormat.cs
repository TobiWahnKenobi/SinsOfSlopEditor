namespace SinsEditor.Core.Documents;

public enum SinsFileFormat
{
    Unknown,
    Text,
    Binary,
}

public static class SinsFileFormatDetector
{
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>Sins files start with a <c>TXT</c>/<c>TXT2</c> or <c>BIN</c>/<c>BIN2</c> marker, text possibly after a UTF-8 BOM.</summary>
    public static SinsFileFormat Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(Utf8Bom)) bytes = bytes[3..];
        if (bytes.StartsWith("TXT"u8)) return SinsFileFormat.Text;
        if (bytes.StartsWith("BIN"u8)) return SinsFileFormat.Binary;
        return SinsFileFormat.Unknown;
    }

    public static SinsFileFormat Detect(string path)
    {
        Span<byte> head = stackalloc byte[8];
        using var stream = File.OpenRead(path);
        int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return Detect(head[..read]);
    }
}
