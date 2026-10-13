using System.Security.Cryptography;
using System.Text;
using SinsEditor.Core.Documents;

namespace SinsEditor.Core.Conversion;

/// <summary>
/// Text versions of binary files, converted with ConvertData and kept outside any mod
/// (default <c>%LocalAppData%\SinsEditor\cache</c>). Safe to delete at any time.
/// A cached file is reused while its timestamp matches the source file.
/// </summary>
public sealed class TextCache(ConvertDataTool tool, string cacheRoot)
{
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SinsEditor", "cache");

    public string CacheRoot { get; } = cacheRoot;

    /// <summary>Path of a readable text version: the file itself if it's already text, otherwise its cached conversion.</summary>
    public async Task<string> GetTextPathAsync(string absolutePath, CancellationToken ct = default)
    {
        if (SinsFileFormatDetector.Detect(absolutePath) != SinsFileFormat.Binary) return absolutePath;

        string cached = CachePathFor(absolutePath);
        var sourceTime = File.GetLastWriteTimeUtc(absolutePath);
        if (File.Exists(cached) && File.GetLastWriteTimeUtc(cached) == sourceTime) return cached;

        await tool.ConvertAsync(absolutePath, cached, ConvertTarget.Text, ct);
        File.SetLastWriteTimeUtc(cached, sourceTime);
        return cached;
    }

    /// <summary>Converts many files in parallel; returns text paths keyed by source path. Failures are reported, not thrown.</summary>
    public async Task<CacheBatchResult> GetTextPathsAsync(
        IReadOnlyCollection<string> absolutePaths, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var paths = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var failures = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int done = 0;
        await Parallel.ForEachAsync(absolutePaths,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
            async (path, token) =>
            {
                try
                {
                    paths[path] = await GetTextPathAsync(path, token);
                }
                catch (Exception e) when (e is ConvertDataException or IOException)
                {
                    failures[path] = e.Message;
                }
                progress?.Report(Interlocked.Increment(ref done));
            });
        return new CacheBatchResult(paths, failures);
    }

    private string CachePathFor(string absolutePath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(absolutePath))!;
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dir.ToLowerInvariant())))[..16];
        return Path.Combine(CacheRoot, hash, Path.GetFileName(absolutePath));
    }
}

public sealed record CacheBatchResult(
    IReadOnlyDictionary<string, string> TextPaths,
    IReadOnlyDictionary<string, string> Failures);
