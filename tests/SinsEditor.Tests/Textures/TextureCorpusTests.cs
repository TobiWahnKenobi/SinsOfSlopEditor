using SinsEditor.Core.Textures;
using SinsEditor.Tests.Index;
using Xunit.Abstractions;

namespace SinsEditor.Tests.Textures;

public class TextureCorpusTests(TestModIndexFixture fixture, ITestOutputHelper output) : IClassFixture<TestModIndexFixture>
{
    [SkippableFact]
    [Trait("Category", "Slow")]
    public void Every_texture_decodes()
    {
        Skip.If(fixture.Index is null, "Game directory or test mod not configured.");

        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(fixture.Index!.Textures.Values, file =>
        {
            try
            {
                var image = TextureLoader.Load(file.AbsolutePath);
                if (image.Width == 0 || image.Height == 0) failures.Add($"{file.RelativePath}: empty");
            }
            catch (TextureLoadException e)
            {
                failures.Add($"{file.RelativePath} ({file.Layer.Name}): {e.Message}");
            }
        });

        output.WriteLine($"{fixture.Index.Textures.Count} textures, {failures.Count} failed");
        foreach (var f in failures.Order().Take(30)) output.WriteLine("  " + f);
        Assert.Empty(failures);
    }

    [SkippableFact]
    [Trait("Category", "Slow")]
    public void Brushes_render_and_report_problems()
    {
        Skip.If(fixture.Index is null, "Game directory or test mod not configured.");

        var store = new TextureStore(fixture.Index!) { Capacity = 512 };
        var results = fixture.Index!.Brushes.Entries.Keys.Select(name => (name, result: store.RenderBrush(name))).ToList();

        var errors = results.Where(r => r.result.Error != null).ToList();
        output.WriteLine($"{results.Count} brushes, {results.Count(r => r.result.Image != null && r.result.Error == null)} ok, {errors.Count} with problems");
        foreach (var g in errors.GroupBy(e => e.result.Error!.Contains("outside") ? "outside texture" : e.result.Error!.Contains("doesn't exist") ? "missing texture" : "other"))
        {
            output.WriteLine($"  {g.Key}: {g.Count()}");
            foreach (var e in g.Take(60)) output.WriteLine("    " + e.result.Error);
        }

        // Most brushes must render; individual broken ones are mod problems, reported by validation.
        Assert.True(errors.Count < results.Count / 10, $"{errors.Count} of {results.Count} brushes failed to render.");
    }

    /// <summary>Writes a few icons as PNG to %TEMP% for a visual check of colour channels and cropping.</summary>
    [SkippableFact]
    public void Exports_sample_icons()
    {
        Skip.If(fixture.Index is null, "Game directory or test mod not configured.");

        var store = new TextureStore(fixture.Index!);
        string dir = Path.Combine(Path.GetTempPath(), "SinsEditorTests", "icons");
        Directory.CreateDirectory(dir);
        foreach (string name in (string[])["HUDICON_b5ships_CENTAURI_CENTURION", "CAPITALSHIPPICTURE_EA_OMEGA", "ResearchScreenBlockPictureCombat0Tech", "ResearchWindowArrowRight1"])
        {
            var result = store.RenderBrush(name);
            Assert.True(result.Image != null, result.Error);
            PngWriter.Save(result.Image!, Path.Combine(dir, name + ".png"));
        }
        output.WriteLine(dir);
    }
}
