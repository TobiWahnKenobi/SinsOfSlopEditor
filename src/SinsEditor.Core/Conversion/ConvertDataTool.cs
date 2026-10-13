using System.Diagnostics;
using SinsEditor.Core.Documents;

namespace SinsEditor.Core.Conversion;

public enum ConvertTarget
{
    Text,
    Binary,
}

public sealed class ConvertDataException(string message) : Exception(message);

/// <summary>
/// Wraps the game's <c>ConvertData_Rebellion.exe</c> (usage: <c>convertdata type src dest [txt|bin]</c>).
/// Only <c>.entity</c>, <c>.mesh</c>, <c>.particle</c> and <c>.brushes</c> files exist in binary form.
/// </summary>
public sealed class ConvertDataTool
{
    public const string ExeName = "ConvertData_Rebellion.exe";

    private static readonly Dictionary<string, string> TypesByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".entity"] = "entity",
        [".mesh"] = "mesh",
        [".particle"] = "particle",
        [".brushes"] = "brushes",
    };

    public ConvertDataTool(string exePath)
    {
        if (!File.Exists(exePath)) throw new FileNotFoundException("ConvertData not found.", exePath);
        ExePath = exePath;
    }

    public string ExePath { get; }

    public static ConvertDataTool FromGameDirectory(string gameDir) => new(Path.Combine(gameDir, ExeName));

    public static bool CanConvert(string path) => TypesByExtension.ContainsKey(Path.GetExtension(path));

    public async Task ConvertAsync(string source, string destination, ConvertTarget target, CancellationToken ct = default)
    {
        if (!TypesByExtension.TryGetValue(Path.GetExtension(source), out string? type))
            throw new ConvertDataException($"ConvertData can't convert '{Path.GetFileName(source)}'.");
        // The tool crashes instead of reporting an error when the source is missing.
        if (!File.Exists(source)) throw new FileNotFoundException("Source file not found.", source);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        // Never let a stale output from an earlier run look like success.
        File.Delete(destination);

        var psi = new ProcessStartInfo(ExePath)
        {
            WorkingDirectory = Path.GetDirectoryName(ExePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(type);
        psi.ArgumentList.Add(Path.GetFullPath(source));
        psi.ArgumentList.Add(Path.GetFullPath(destination));
        psi.ArgumentList.Add(target == ConvertTarget.Text ? "txt" : "bin");

        using var process = Process.Start(psi) ?? throw new ConvertDataException("ConvertData couldn't be started.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            throw;
        }

        string log = ((await stdout) + (await stderr)).Trim();
        if (process.ExitCode != 0 || !File.Exists(destination))
            throw new ConvertDataException(
                $"ConvertData failed on '{Path.GetFileName(source)}' (exit code {process.ExitCode}). {log}".Trim());

        var expected = target == ConvertTarget.Text ? SinsFileFormat.Text : SinsFileFormat.Binary;
        if (SinsFileFormatDetector.Detect(destination) != expected)
            throw new ConvertDataException($"ConvertData output for '{Path.GetFileName(source)}' isn't {target.ToString().ToLowerInvariant()}.");
    }
}
