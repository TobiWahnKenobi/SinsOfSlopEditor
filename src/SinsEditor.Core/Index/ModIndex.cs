using System.Collections.Concurrent;
using SinsEditor.Core.Conversion;
using SinsEditor.Core.Documents;
using SinsEditor.Core.FileSystem;
using SinsEditor.Core.Validation;

namespace SinsEditor.Core.Index;

public enum ReferenceKind
{
    Entity,
    String,
    Brush,
    Mesh,
    Particle,
    Texture,
    Sound,
}

/// <summary>An entity file. <paramref name="InManifest"/> false means the game doesn't load it (it isn't in the effective entity.manifest).</summary>
public sealed record EntityRecord(string Name, string EntityType, ResolvedFile File, SinsDocument Document, bool InManifest);

/// <summary>
/// A value in an entity that points at something else. <paramref name="Context"/> is the qualified key
/// (<c>parentKey/key</c>) the reference was recognised by. <paramref name="Resolved"/> is false when the target doesn't exist.
/// </summary>
public sealed record Reference(EntityRecord Source, int LineIndex, string Key, string Context, ReferenceKind Kind, string Target, bool Resolved);

/// <summary>An entity listed in a race's <c>Player</c> entity, e.g. section <c>frigateInfo</c>, page <c>Page:0</c>.</summary>
public sealed record RaceItem(EntityRecord Race, string Section, string? Page, string Name, EntityRecord? Entity);

public sealed record IndexProgress(string Stage, int Done, int Total);

/// <summary>Everything the editor knows about the effective mod: entities, strings, assets, references, ownership.</summary>
public sealed class ModIndex
{
    private const string EntityManifest = "entity.manifest";
    private const string BrushManifest = "brush.manifest";

    private readonly Dictionary<string, EntityRecord> _entities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ResolvedFile> _meshes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ResolvedFile> _particles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ResolvedFile> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ReferenceKind> _referenceKeys = new(StringComparer.Ordinal);
    private readonly List<Reference> _references = [];
    private readonly Dictionary<EntityRecord, List<Reference>> _outgoing = [];
    private readonly Dictionary<(ReferenceKind, string), List<Reference>> _incoming = new(new KindNameComparer());
    private readonly List<RaceItem> _raceItems = [];
    private readonly List<Problem> _problems = [];
    private readonly List<string> _manifestEntries = [];

    private ModIndex(LayerStack stack, string language)
    {
        Stack = stack;
        Language = language;
        Strings = StringTable.Empty(language);
    }

    public LayerStack Stack { get; }
    public string Language { get; }
    public StringTable Strings { get; private set; }
    public NamedEntryCatalog Brushes { get; } = new();
    public NamedEntryCatalog Sounds { get; } = new();

    /// <summary>Every entity file in any layer, including ones the game doesn't load.</summary>
    public IReadOnlyDictionary<string, EntityRecord> Entities => _entities;
    /// <summary>Entities the game loads: listed in the effective entity.manifest.</summary>
    public IEnumerable<EntityRecord> ActiveEntities => _entities.Values.Where(e => e.InManifest);
    public IReadOnlyDictionary<string, ResolvedFile> Meshes => _meshes;
    public IReadOnlyDictionary<string, ResolvedFile> Particles => _particles;
    public IReadOnlyDictionary<string, ResolvedFile> Textures => _textures;
    /// <summary>Entity file names as listed in the effective <c>entity.manifest</c>.</summary>
    public IReadOnlyList<string> ManifestEntries => _manifestEntries;
    /// <summary>
    /// Qualified keys (<c>parentKey/key</c>, or <c>/key</c> at top level) recognised as references, and what they point to.
    /// Inferred from the data.
    /// </summary>
    public IReadOnlyDictionary<string, ReferenceKind> ReferenceKeys => _referenceKeys;
    public IReadOnlyList<Reference> References => _references;
    public IReadOnlyList<RaceItem> RaceItems => _raceItems;
    public IReadOnlyList<Problem> Problems => _problems;

    public IEnumerable<EntityRecord> Races => ActiveEntities.Where(e => e.EntityType == "Player");

    public IReadOnlyList<Reference> OutgoingReferences(EntityRecord entity) =>
        _outgoing.TryGetValue(entity, out var list) ? list : [];

    public IReadOnlyList<Reference> IncomingReferences(ReferenceKind kind, string name) =>
        _incoming.TryGetValue((kind, name), out var list) ? list : [];

    public IEnumerable<RaceItem> ItemsOf(EntityRecord race) => _raceItems.Where(i => i.Race == race);

    public IEnumerable<EntityRecord> RacesOwning(string entityName) =>
        _raceItems.Where(i => string.Equals(i.Name, entityName, StringComparison.OrdinalIgnoreCase)).Select(i => i.Race).Distinct();

    public bool Exists(ReferenceKind kind, string name) => kind switch
    {
        ReferenceKind.Entity => _entities.TryGetValue(name, out var e) && e.InManifest,
        ReferenceKind.String => Strings.Contains(name),
        ReferenceKind.Brush => Brushes.Contains(name),
        ReferenceKind.Mesh => _meshes.ContainsKey(StripExtension(name, ".mesh")),
        ReferenceKind.Particle => _particles.ContainsKey(StripExtension(name, ".particle")),
        ReferenceKind.Texture => _textures.ContainsKey(StripExtension(StripExtension(name, ".dds"), ".tga")),
        ReferenceKind.Sound => Sounds.Contains(name),
        _ => false,
    };

    /// <summary>Asset references may or may not include the file extension (<c>meshName "Sentry.mesh"</c>).</summary>
    private static string StripExtension(string name, string extension) =>
        name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? name[..^extension.Length] : name;

    /// <summary>Display name of an entity from the string table, if it has one.</summary>
    public string? DisplayName(EntityRecord entity)
    {
        foreach (string key in (string[])["NameStringID", "raceNameStringID", "nameStringID"])
        {
            var line = entity.Document.Lines.FirstOrDefault(l => l.Key == key);
            if (line != null && SinsValue.TryGetString(line.Value, out var id) && Strings.Get(id) is { } name) return name;
        }
        return null;
    }

    public static async Task<ModIndex> BuildAsync(
        LayerStack stack, TextCache cache, string language,
        IProgress<IndexProgress>? progress = null, CancellationToken ct = default)
    {
        var index = new ModIndex(stack, language);
        await index.LoadEntitiesAsync(cache, progress, ct);
        await index.LoadStringsAsync(cache, ct);
        await index.LoadBrushesAsync(cache, progress, ct);
        index.LoadSounds();
        index.LoadAssets();
        progress?.Report(new IndexProgress("Linking references", 0, 1));
        index.InferReferenceKeys();
        index.CollectReferences();
        index.CollectRaceItems();
        progress?.Report(new IndexProgress("Linking references", 1, 1));
        return index;
    }

    private async Task LoadEntitiesAsync(TextCache cache, IProgress<IndexProgress>? progress, CancellationToken ct)
    {
        var manifestNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Stack.Resolve(EntityManifest) is { } manifest)
        {
            foreach (var line in SinsDocument.Load(manifest.AbsolutePath).Lines.Where(l => l.Key == "entityName"))
            {
                if (!SinsValue.TryGetString(line.Value, out var file)) continue;
                _manifestEntries.Add(file);
                manifestNames.Add(Path.GetFileNameWithoutExtension(file));
            }
        }
        else
        {
            _problems.Add(new Problem(ProblemSeverity.Error, ProblemCodes.MissingManifest, "No entity.manifest found in any layer."));
        }

        var files = Stack.EnumerateFolder("GameInfo", ".entity")
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f.RelativePath), StringComparer.OrdinalIgnoreCase);

        foreach (string name in manifestNames.Where(n => !files.ContainsKey(n)))
        {
            _problems.Add(new Problem(ProblemSeverity.Error, ProblemCodes.ManifestEntryWithoutFile,
                $"entity.manifest lists '{name}.entity', but no layer has that file.", Stack.Resolve(EntityManifest)?.RelativePath));
        }

        var sources = files.Values.Select(f => f.AbsolutePath).ToList();
        var converted = await cache.GetTextPathsAsync(sources,
            new Progress<int>(n => progress?.Report(new IndexProgress("Converting entities", n, sources.Count))), ct);
        AddConversionFailures(converted, files.Values);

        var records = new ConcurrentBag<EntityRecord>();
        int parsed = 0;
        Parallel.ForEach(files, new ParallelOptions { CancellationToken = ct }, kv =>
        {
            if (converted.TextPaths.TryGetValue(kv.Value.AbsolutePath, out var textPath))
            {
                var doc = SinsDocument.Load(textPath);
                string type = doc.Lines.FirstOrDefault(l => l.Key == "entityType") is { } t && SinsValue.TryGetString(t.Value, out var s) ? s : "";
                records.Add(new EntityRecord(kv.Key, type, kv.Value, doc, manifestNames.Contains(kv.Key)));
            }
            progress?.Report(new IndexProgress("Reading entities", Interlocked.Increment(ref parsed), files.Count));
        });

        foreach (var record in records.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            _entities[record.Name] = record;
            // Unlisted files in lower layers are entities the edited mod dropped on purpose.
            if (!record.InManifest && record.File.Layer.IsEditable)
            {
                _problems.Add(new Problem(ProblemSeverity.Warning, ProblemCodes.EntityNotInManifest,
                    $"'{record.Name}' isn't listed in entity.manifest, so the game won't load it.", record.File.RelativePath));
            }
        }
    }

    private async Task LoadStringsAsync(TextCache cache, CancellationToken ct)
    {
        if (Languages.ResolveStringFile(Stack, Language) is not { } file) return;
        var doc = SinsDocument.Load(await cache.GetTextPathAsync(file.AbsolutePath, ct));
        Strings = StringTable.Load(Language, file, doc);
        foreach (var dup in Strings.Duplicates)
        {
            _problems.Add(new Problem(ProblemSeverity.Info, ProblemCodes.DuplicateString,
                $"String ID '{dup.Id}' is defined more than once; the first definition is used.", file.RelativePath, dup.IdLineIndex));
        }
        if (Strings.DeclaredCount is { } declared && declared != Strings.Entries.Count + Strings.Duplicates.Count)
        {
            _problems.Add(new Problem(ProblemSeverity.Error, ProblemCodes.StringCountMismatch,
                $"NumStrings says {declared}, but the file has {Strings.Entries.Count + Strings.Duplicates.Count} strings.", file.RelativePath));
        }
    }

    private async Task LoadBrushesAsync(TextCache cache, IProgress<IndexProgress>? progress, CancellationToken ct)
    {
        if (Stack.Resolve(BrushManifest) is not { } manifest) return;
        var brushFiles = new List<ResolvedFile>();
        foreach (var line in SinsDocument.Load(manifest.AbsolutePath).Lines.Where(l => l.Key == "brushFile"))
        {
            if (!SinsValue.TryGetString(line.Value, out var name)) continue;
            if (Stack.Resolve("Window/" + name) is { } file) brushFiles.Add(file);
            else _problems.Add(new Problem(ProblemSeverity.Error, ProblemCodes.MissingBrushFile,
                $"brush.manifest lists '{name}', but no layer has Window/{name}.", manifest.RelativePath));
        }

        var converted = await cache.GetTextPathsAsync(brushFiles.Select(f => f.AbsolutePath).ToList(),
            new Progress<int>(n => progress?.Report(new IndexProgress("Converting brushes", n, brushFiles.Count))), ct);
        AddConversionFailures(converted, brushFiles);

        foreach (var file in brushFiles)
        {
            if (!converted.TextPaths.TryGetValue(file.AbsolutePath, out var textPath)) continue;
            Brushes.AddFrom(file, SinsDocument.Load(textPath), "brush");
        }
        foreach (var dup in Brushes.Duplicates)
        {
            _problems.Add(new Problem(ProblemSeverity.Warning, ProblemCodes.DuplicateBrush,
                $"Brush '{dup.Name}' is defined more than once; the first definition is used.", dup.File.RelativePath, dup.Node.LineIndex));
        }
    }

    private void LoadSounds()
    {
        foreach (var file in Stack.EnumerateFolder("GameInfo", ".sounddata"))
            Sounds.AddFrom(file, SinsDocument.Load(file.AbsolutePath), "effect");
    }

    private void LoadAssets()
    {
        foreach (var f in Stack.EnumerateFolder("Mesh", ".mesh")) _meshes[Path.GetFileNameWithoutExtension(f.RelativePath)] = f;
        foreach (var f in Stack.EnumerateFolder("Particle", ".particle")) _particles[Path.GetFileNameWithoutExtension(f.RelativePath)] = f;
        foreach (var f in Stack.EnumerateFolder("Textures"))
        {
            string ext = Path.GetExtension(f.RelativePath);
            if (ext.Equals(".dds", StringComparison.OrdinalIgnoreCase) || ext.Equals(".tga", StringComparison.OrdinalIgnoreCase))
                _textures.TryAdd(Path.GetFileNameWithoutExtension(f.RelativePath), f);
        }
    }

    /// <summary>
    /// Decides which property names hold references: a key whose non-empty string values mostly (≥ 50 %)
    /// resolve to one kind of target is treated as a reference of that kind.
    /// </summary>
    private void InferReferenceKeys()
    {
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        var hits = new Dictionary<(string Key, ReferenceKind Kind), int>();
        foreach (var (context, value) in StringValues())
        {
            totals[context] = totals.GetValueOrDefault(context) + 1;
            foreach (var kind in Enum.GetValues<ReferenceKind>())
            {
                if (Exists(kind, value)) hits[(context, kind)] = hits.GetValueOrDefault((context, kind)) + 1;
            }
        }
        foreach (var group in hits.GroupBy(h => h.Key.Key))
        {
            var best = group.MaxBy(h => h.Value);
            if (best.Value * 2 >= totals[group.Key]) _referenceKeys[group.Key] = best.Key.Kind;
        }
    }

    private void CollectReferences()
    {
        foreach (var entity in ActiveEntities)
        {
            var outgoing = new List<Reference>();
            var lines = entity.Document.Lines;
            var parents = entity.Document.GetParentIndices();
            for (int i = 0; i < lines.Count; i++)
            {
                string context = Context(lines, parents, i);
                if (!_referenceKeys.TryGetValue(context, out var kind)) continue;
                if (!SinsValue.TryGetString(lines[i].Value, out var target) || target.Length == 0) continue;
                var reference = new Reference(entity, i, lines[i].Key, context, kind, target, Exists(kind, target));
                outgoing.Add(reference);
                _references.Add(reference);
                if (!_incoming.TryGetValue((kind, target), out var list)) _incoming[(kind, target)] = list = [];
                list.Add(reference);
                if (!reference.Resolved)
                {
                    _problems.Add(new Problem(ProblemSeverity.Error, ProblemCodes.MissingReference,
                        $"{entity.Name}: {lines[i].Key} points to {kind.ToString().ToLowerInvariant()} '{target}', which doesn't exist.",
                        entity.File.RelativePath, i));
                }
            }
            _outgoing[entity] = outgoing;
        }
    }

    private void CollectRaceItems()
    {
        foreach (var race in Races)
        {
            if (race.Document.BuildTree().Child("entities") is not { } entities) continue;
            foreach (var section in entities.Children)
            {
                foreach (var node in section.Descendants().Where(n => n.Key == "entityDefName"))
                {
                    if (!SinsValue.TryGetString(node.Value, out var name) || name.Length == 0) continue;
                    string? page = node.Parent != section ? node.Parent?.Key : null;
                    _raceItems.Add(new RaceItem(race, section.Key, page, name, _entities.GetValueOrDefault(name)));
                }
            }
        }
    }

    /// <summary>Non-empty string values of active entities with their qualified key.</summary>
    private IEnumerable<(string Context, string Value)> StringValues()
    {
        foreach (var entity in ActiveEntities)
        {
            var lines = entity.Document.Lines;
            var parents = entity.Document.GetParentIndices();
            for (int i = 0; i < lines.Count; i++)
            {
                if (SinsValue.TryGetString(lines[i].Value, out var value) && value.Length > 0)
                    yield return (Context(lines, parents, i), value);
            }
        }
    }

    private static string Context(List<SinsLine> lines, int[] parents, int i) =>
        (parents[i] >= 0 ? lines[parents[i]].Key : "") + "/" + lines[i].Key;

    private void AddConversionFailures(CacheBatchResult result, IEnumerable<ResolvedFile> files)
    {
        foreach (var file in files)
        {
            if (result.Failures.TryGetValue(file.AbsolutePath, out var message))
                _problems.Add(new Problem(ProblemSeverity.Error, ProblemCodes.ConversionFailed, message, file.RelativePath));
        }
    }

    private sealed class KindNameComparer : IEqualityComparer<(ReferenceKind Kind, string Name)>
    {
        public bool Equals((ReferenceKind Kind, string Name) x, (ReferenceKind Kind, string Name) y) =>
            x.Kind == y.Kind && StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);

        public int GetHashCode((ReferenceKind Kind, string Name) obj) =>
            HashCode.Combine(obj.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }
}
