using SinsEditor.Core.Conversion;
using SinsEditor.Core.FileSystem;
using SinsEditor.Core.Index;
using SinsEditor.Core.Textures;
using SinsEditor.Core.Validation;

namespace SinsEditor.Core;

public sealed record WorkspaceOptions(string GameDirectory, string EditedModPath, IReadOnlyList<string> BaseModPaths, string? Language);

/// <summary>An opened mod: its layers, the index, textures and the problems found.</summary>
public sealed class ModWorkspace
{
    private ModWorkspace(WorkspaceOptions options, LayerStack stack, ModIndex index, TextureStore textures,
        IReadOnlyList<ConventionMismatch> conventionMismatches, IReadOnlyList<Problem> problems)
    {
        Options = options;
        Stack = stack;
        Index = index;
        Textures = textures;
        ConventionMismatches = conventionMismatches;
        Problems = problems;
    }

    public WorkspaceOptions Options { get; }
    public LayerStack Stack { get; }
    public ModIndex Index { get; }
    public TextureStore Textures { get; }
    public IReadOnlyList<ConventionMismatch> ConventionMismatches { get; }
    /// <summary>Index problems plus workspace-level checks (conventions, brush images).</summary>
    public IReadOnlyList<Problem> Problems { get; }

    public string ModName => Path.GetFileName(Options.EditedModPath);

    /// <summary>Warnings and errors, counting each file with wrong encoding or line endings separately.</summary>
    public int ProblemCount =>
        Problems.Count(p => p.Severity != ProblemSeverity.Info && p.Code != ProblemCodes.ConventionMismatch) + ConventionMismatches.Count;

    public static LayerStack CreateStack(WorkspaceOptions options) => new(
    [
        new Layer("Base game", options.GameDirectory, LayerKind.BaseGame),
        .. options.BaseModPaths.Select(p => new Layer(Path.GetFileName(p), p, LayerKind.BaseMod)),
        new Layer(Path.GetFileName(options.EditedModPath), options.EditedModPath, LayerKind.EditedMod),
    ]);

    public static async Task<ModWorkspace> OpenAsync(
        WorkspaceOptions options, IProgress<IndexProgress>? progress = null, CancellationToken ct = default, string? cacheRoot = null)
    {
        var stack = CreateStack(options);
        var tool = ConvertDataTool.FromGameDirectory(options.GameDirectory);
        var cache = new TextCache(tool, cacheRoot ?? TextCache.DefaultRoot);

        string language = options.Language is { } l && Languages.Available(stack).Contains(l, StringComparer.OrdinalIgnoreCase)
            ? l
            : Languages.Available(stack).FirstOrDefault(n => n.Equals("English", StringComparison.OrdinalIgnoreCase))
              ?? Languages.Available(stack).FirstOrDefault() ?? "English";

        var index = await ModIndex.BuildAsync(stack, cache, language, progress, ct);
        var textures = new TextureStore(index);

        progress?.Report(new IndexProgress("Checking files", 0, 1));
        var (mismatches, brushProblems) = await Task.Run(() => (ConventionCheck.Find(stack), CheckBrushes(index, textures)), ct);
        progress?.Report(new IndexProgress("Checking files", 1, 1));

        var problems = index.Problems.Concat(brushProblems).ToList();
        if (mismatches.Count > 0) problems.Add(ConventionCheck.ToProblem(mismatches));
        return new ModWorkspace(options, stack, index, textures, mismatches, problems);
    }

    /// <summary>Brushes of the edited mod whose image is missing or lies outside its texture.</summary>
    private static List<Problem> CheckBrushes(ModIndex index, TextureStore textures)
    {
        var problems = new List<Problem>();
        foreach (var entry in index.Brushes.Entries.Values.Where(e => e.File.Layer.IsEditable))
        {
            var result = textures.RenderBrush(entry.Name);
            if (result.Error != null)
                problems.Add(new Problem(ProblemSeverity.Warning, ProblemCodes.BrushImageProblem, result.Error, entry.File.RelativePath, entry.Node.LineIndex));
        }
        return problems;
    }
}
