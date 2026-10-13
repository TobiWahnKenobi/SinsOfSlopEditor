using SinsEditor.Core.Documents;

namespace SinsEditor.Core.Editing;

/// <summary>Writes to a temporary file next to the target and then moves it into place, so a failure never leaves a half-written file.</summary>
public static class SafeWrite
{
    public const string TempSuffix = ".sinseditor-tmp";

    public static void Text(SinsDocument document, string target) => Bytes(document.ToBytes(), target);

    public static void Bytes(byte[] bytes, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temp = target + TempSuffix;
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
