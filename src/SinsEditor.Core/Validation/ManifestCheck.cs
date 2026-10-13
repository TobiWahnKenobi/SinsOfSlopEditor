using SinsEditor.Core.Documents;
using SinsEditor.Core.FileSystem;
using SinsEditor.Core.Index;

namespace SinsEditor.Core.Validation;

/// <summary>
/// The edited mod's <c>*.manifest</c> files: the counter on the first line must match the entries (the game reads exactly
/// that many), entries shouldn't repeat, and listed files must exist. entity and brush manifest entries are checked
/// for existence while indexing.
/// </summary>
public static class ManifestCheck
{
    /// <summary>Where the files a manifest lists live, for manifests not covered elsewhere.</summary>
    private static readonly Dictionary<string, (string Folder, string Extension)> Targets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["galaxy.manifest"] = ("Galaxy", ".galaxy"),
        ["playerPictures.manifest"] = ("GameInfo", ""),
        ["playerThemes.manifest"] = ("GameInfo", ""),
        ["skybox.manifest"] = ("Mesh", ""),
    };

    public static IEnumerable<Problem> Check(LayerStack stack)
    {
        if (stack.EditedMod is not { } mod) yield break;
        foreach (string path in mod.Files.Keys.Where(p => !p.Contains('/') && p.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)))
        {
            if (mod.GetAbsolutePath(path) is not { } abs) continue;
            SinsDocument doc;
            try { doc = SinsDocument.Load(abs); }
            catch (Exception e) when (e is IOException or InvalidDataException) { continue; }
            if (!doc.Header.StartsWith("TXT", StringComparison.Ordinal)) continue;

            var lines = doc.Lines;
            int counter = lines.FindIndex(1, l => !l.IsBlank);
            if (counter < 0 || !SinsValue.TryGetInt(lines[counter].Value, out int declared)) continue;
            var entries = Enumerable.Range(counter + 1, lines.Count - counter - 1).Where(i => !lines[i].IsBlank).ToList();
            if (declared != entries.Count)
            {
                yield return new Problem(ProblemSeverity.Error, ProblemCodes.ManifestCountMismatch,
                    $"{path}: {lines[counter].Key} says {declared}, but it lists {entries.Count} entries. The game reads exactly {declared}.",
                    path, counter);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (int i in entries)
            {
                if (!SinsValue.TryGetString(lines[i].Value, out var name) || name.Length == 0) continue;
                if (!seen.Add(name))
                {
                    yield return new Problem(ProblemSeverity.Warning, ProblemCodes.DuplicateManifestEntry,
                        $"{path} lists '{name}' more than once.", path, i);
                }
                else if (Targets.TryGetValue(path, out var target)
                         && stack.Resolve($"{target.Folder}/{name}{(Path.HasExtension(name) ? "" : target.Extension)}") is null)
                {
                    yield return new Problem(ProblemSeverity.Error, ProblemCodes.ManifestEntryWithoutFile,
                        $"{path} lists '{name}', but no layer has it in {target.Folder}/.", path, i);
                }
            }
        }
    }

    /// <summary>
    /// The edited mod's active entities nothing refers to, for types whose base-game entities are nearly always referred to
    /// (so an unreferenced one is most likely forgotten, not a root like a player race).
    /// </summary>
    public static IEnumerable<Problem> UnusedEntities(ModIndex index)
    {
        var referenced = new HashSet<string>(index.References.Where(r => r.Kind == ReferenceKind.Entity && r.Resolved).Select(r => r.Target),
            StringComparer.OrdinalIgnoreCase);
        referenced.UnionWith(index.RaceItems.Select(i => i.Name));

        var typesUsuallyReferenced = index.ActiveEntities
            .Where(e => !e.File.Layer.IsEditable)
            .GroupBy(e => e.EntityType)
            .Where(g => g.Count() >= 5 && g.Count(e => referenced.Contains(e.Name)) * 20 >= g.Count() * 19)
            .Select(g => g.Key)
            .ToHashSet();

        foreach (var entity in index.ActiveEntities.Where(e => e.File.Layer.IsEditable && typesUsuallyReferenced.Contains(e.EntityType)))
        {
            if (referenced.Contains(entity.Name)) continue;
            yield return new Problem(ProblemSeverity.Info, ProblemCodes.UnusedEntity,
                $"Nothing refers to {entity.Name} ({entity.EntityType}), so the game never uses it.", entity.File.RelativePath);
        }
    }
}
