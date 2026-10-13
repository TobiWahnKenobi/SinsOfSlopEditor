using SinsEditor.Core.Documents;
using Xunit.Abstractions;

namespace SinsEditor.Tests.Documents;

/// <summary>Parses and re-serializes every text file of a real mod and requires identical bytes.</summary>
public class CorpusRoundTripTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Every_text_file_in_test_mod_round_trips()
    {
        string? mod = TestPaths.TestMod;
        Skip.If(mod is null, "No test mod configured (SINSEDITOR_TEST_MOD or testpaths.local.json).");

        int text = 0, binary = 0, other = 0;
        var failures = new List<string>();
        foreach (string path in Directory.EnumerateFiles(mod, "*", SearchOption.AllDirectories))
        {
            byte[] bytes = File.ReadAllBytes(path);
            switch (SinsFileFormatDetector.Detect(bytes))
            {
                case SinsFileFormat.Binary: binary++; continue;
                case SinsFileFormat.Unknown: other++; continue;
            }
            text++;
            byte[] written = SinsDocument.Parse(bytes).ToBytes();
            if (!bytes.AsSpan().SequenceEqual(written)) failures.Add(Path.GetRelativePath(mod, path));
        }

        output.WriteLine($"text {text}, binary {binary}, other {other}");
        Assert.True(text > 0, "No text files found.");
        Assert.True(failures.Count == 0, "Not byte-identical:\n" + string.Join("\n", failures.Take(20)));
    }
}
