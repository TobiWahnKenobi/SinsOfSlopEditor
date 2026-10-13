using System.Collections.Concurrent;
using SinsEditor.Core.Conversion;
using SinsEditor.Core.Documents;
using Xunit.Abstractions;

namespace SinsEditor.Tests.Conversion;

/// <summary>
/// Converts every binary base-game file to text with ConvertData and checks that the parser round-trips the result.
/// Slow (a few thousand process starts), so it's tagged and can be filtered out with <c>--filter Category!=Slow</c>.
/// </summary>
public class GameCorpusTests(ITestOutputHelper output)
{
    [SkippableFact]
    [Trait("Category", "Slow")]
    public async Task Every_converted_game_file_round_trips()
    {
        string? game = TestPaths.GameDir;
        Skip.If(game is null, "No game directory configured (SINSEDITOR_GAME_DIR or testpaths.local.json).");

        var tool = ConvertDataTool.FromGameDirectory(game);
        string temp = Path.Combine(Path.GetTempPath(), "SinsEditorTests", Guid.NewGuid().ToString("N"));
        var sources = Directory.EnumerateFiles(game, "*", SearchOption.AllDirectories)
            .Where(ConvertDataTool.CanConvert)
            .Where(p => SinsFileFormatDetector.Detect(p) == SinsFileFormat.Binary)
            .ToList();
        var failures = new ConcurrentBag<string>();

        try
        {
            await Parallel.ForEachAsync(sources, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                async (source, ct) =>
                {
                    string rel = Path.GetRelativePath(game, source);
                    string dest = Path.Combine(temp, rel);
                    try
                    {
                        await tool.ConvertAsync(source, dest, ConvertTarget.Text, ct);
                        byte[] bytes = await File.ReadAllBytesAsync(dest, ct);
                        if (!bytes.AsSpan().SequenceEqual(SinsDocument.Parse(bytes).ToBytes()))
                            failures.Add(rel + ": not byte-identical");
                    }
                    catch (Exception e)
                    {
                        failures.Add(rel + ": " + e.Message);
                    }
                });
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }

        output.WriteLine($"converted {sources.Count} binary files");
        Assert.True(sources.Count > 0, "No binary files found.");
        Assert.True(failures.IsEmpty, string.Join("\n", failures.Take(20)));
    }

    [SkippableFact]
    public async Task Text_converted_back_to_binary_matches_original()
    {
        string? game = TestPaths.GameDir;
        Skip.If(game is null, "No game directory configured.");

        var tool = ConvertDataTool.FromGameDirectory(game);
        string temp = Path.Combine(Path.GetTempPath(), "SinsEditorTests", Guid.NewGuid().ToString("N"));
        // One sample per binary type.
        var samples = new[] { "GameInfo", "Mesh", "Particle", "Window" }
            .Select(dir => Directory.EnumerateFiles(Path.Combine(game, dir))
                .First(p => ConvertDataTool.CanConvert(p) && SinsFileFormatDetector.Detect(p) == SinsFileFormat.Binary));

        try
        {
            foreach (string source in samples)
            {
                string txt = Path.Combine(temp, "txt", Path.GetFileName(source));
                string bin = Path.Combine(temp, "bin", Path.GetFileName(source));
                await tool.ConvertAsync(source, txt, ConvertTarget.Text);
                await tool.ConvertAsync(txt, bin, ConvertTarget.Binary);
                Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(bin));
            }
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [SkippableFact]
    public async Task Missing_source_throws_instead_of_crashing_the_tool()
    {
        string? game = TestPaths.GameDir;
        Skip.If(game is null, "No game directory configured.");

        var tool = ConvertDataTool.FromGameDirectory(game);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            tool.ConvertAsync(Path.Combine(Path.GetTempPath(), "does-not-exist.entity"), Path.GetTempFileName() + ".entity", ConvertTarget.Text));
    }
}
