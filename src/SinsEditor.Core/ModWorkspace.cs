using SinsEditor.Core.Conversion;
using SinsEditor.Core.Editing;
using SinsEditor.Core.FileSystem;
using SinsEditor.Core.Index;
using SinsEditor.Core.Textures;
using SinsEditor.Core.Validation;

namespace SinsEditor.Core;

public sealed record WorkspaceOptions(string GameDirectory, string EditedModPath, IReadOnlyList<string> BaseModPaths, string? Language);

/// <summary>An opened mod: its layers, the index, textures, open edit sessions and the problems found.</summary>
public sealed class ModWorkspace
{
    private readonly List<Problem> _brushProblems;
    private readonly List<ConventionMismatch> _conventionMismatches;
    private readonly Dictionary<string, EntityEditSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private List<Problem> _problems = [];
    private ILookup<string, Problem>? _problemsByFile;

    private ModWorkspace(WorkspaceOptions options, LayerStack stack, ModIndex index, TextureStore textures, TextCache cache,
        List<ConventionMismatch> conventionMismatches, List<Problem> brushProblems)
    {
        Options = options;
        Stack = stack;
        Index = index;
        Textures = textures;
        Cache = cache;
        _conventionMismatches = conventionMismatches;
        _brushProblems = brushProblems;
        RecomputeProblems();
    }

    public WorkspaceOptions Options { get; }
    public LayerStack Stack { get; }
    public ModIndex Index { get; }
    public TextureStore Textures { get; }
    public TextCache Cache { get; }
    public IReadOnlyList<ConventionMismatch> ConventionMismatches => _conventionMismatches;
    /// <summary>Index problems plus workspace-level checks (conventions, brush images).</summary>
    public IReadOnlyList<Problem> Problems => _problems;

    public string ModName => Path.GetFileName(Options.EditedModPath);

    private PropertyHints? _hints;

    /// <summary>Value kinds, known values and reference targets per property, built on first use.</summary>
    public PropertyHints Hints => _hints ??= PropertyHints.Build(Index);

    /// <summary>Raised after something was saved, so pages can refresh counts and problems.</summary>
    public event Action? Changed;

    /// <summary>Warnings and errors located in a file (relative path, case-insensitive).</summary>
    public IEnumerable<Problem> ProblemsIn(string relativePath) =>
        (_problemsByFile ??= Problems
            .Where(p => p.RelativePath != null && p.Severity != ProblemSeverity.Info)
            .ToLookup(p => p.RelativePath!, StringComparer.OrdinalIgnoreCase))[relativePath];

    public bool HasProblems(EntityRecord entity) => ProblemsIn(entity.File.RelativePath).Any();

    /// <summary>Warnings and errors, counting each file with wrong encoding or line endings separately.</summary>
    public int ProblemCount =>
        Problems.Count(p => p.Severity != ProblemSeverity.Info && p.Code != ProblemCodes.ConventionMismatch) + ConventionMismatches.Count;

    // ---- Editing ----

    /// <summary>The edit session for an entity, created on first use and shared by every page showing it.</summary>
    public EntityEditSession SessionFor(EntityRecord entity)
    {
        if (!_sessions.TryGetValue(entity.Name, out var session)) _sessions[entity.Name] = session = new EntityEditSession(entity);
        return session;
    }

    public EntityEditSession? ExistingSession(string entityName) => _sessions.GetValueOrDefault(entityName);

    public IEnumerable<EntityEditSession> DirtySessions => _sessions.Values.Where(s => s.IsDirty);

    /// <summary>Format a save would use by default: binary if the edited mod's copy is binary, text otherwise.</summary>
    public SaveFormat DefaultSaveFormat(EntityRecord entity) =>
        Stack.EditedMod?.GetAbsolutePath(entity.File.RelativePath) is { } path
        && Documents.SinsFileFormatDetector.Detect(path) == Documents.SinsFileFormat.Binary
            ? SaveFormat.Binary
            : SaveFormat.Text;

    public async Task SaveAsync(EntityEditSession session, SaveFormat format, CancellationToken ct = default)
    {
        var saved = await new EntitySaver(this).SaveAsync(session, format, ct);
        Index.UpdateEntity(saved);
        _conventionMismatches.RemoveAll(m => string.Equals(m.RelativePath, saved.File.RelativePath, StringComparison.OrdinalIgnoreCase));
        RecomputeProblems();
        session.MarkSaved(saved);
        Changed?.Invoke();
    }

    private void RecomputeProblems()
    {
        _problems = Index.Problems.Concat(_brushProblems).ToList();
        if (_conventionMismatches.Count > 0) _problems.Add(ConventionCheck.ToProblem(_conventionMismatches));
        _problemsByFile = null;
    }

    // ---- Opening ----

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
        var (mismatches, brushProblems) = await Task.Run(() => (ConventionCheck.Find(stack).ToList(), CheckBrushes(index, textures)), ct);
        progress?.Report(new IndexProgress("Checking files", 1, 1));

        return new ModWorkspace(options, stack, index, textures, cache, mismatches, brushProblems);
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
