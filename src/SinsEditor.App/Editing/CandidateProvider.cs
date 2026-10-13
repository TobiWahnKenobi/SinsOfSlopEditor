using SinsEditor.Core;
using SinsEditor.Core.Index;

namespace SinsEditor.App.Editing;

/// <summary>Something a reference can point to, as offered by the picker.</summary>
public sealed record PickerItem(string Value, string Display, string Detail)
{
    public bool Matches(string query) =>
        query.Length == 0
        || Value.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Display.Contains(query, StringComparison.CurrentCultureIgnoreCase);
}

/// <summary>Valid targets per reference kind (and entity types), built once per workspace and kind.</summary>
public sealed class CandidateProvider(ModWorkspace workspace)
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ModWorkspace, CandidateProvider> Providers = [];

    private readonly Dictionary<string, IReadOnlyList<PickerItem>> _cache = new(StringComparer.Ordinal);

    /// <summary>One provider per workspace, so candidate lists are built once and shared by all pages.</summary>
    public static CandidateProvider For(ModWorkspace workspace) => Providers.GetValue(workspace, ws => new CandidateProvider(ws));

    public IReadOnlyList<PickerItem> For(ReferenceKind kind, IReadOnlyList<string> entityTypes)
    {
        string key = kind + "|" + string.Join(",", entityTypes);
        if (!_cache.TryGetValue(key, out var items)) _cache[key] = items = Build(kind, entityTypes);
        return items;
    }

    private IReadOnlyList<PickerItem> Build(ReferenceKind kind, IReadOnlyList<string> entityTypes)
    {
        var index = workspace.Index;
        IEnumerable<PickerItem> items = kind switch
        {
            ReferenceKind.Entity => index.ActiveEntities
                .Where(e => entityTypes.Count == 0 || entityTypes.Contains(e.EntityType))
                .Select(e => new PickerItem(e.Name, index.DisplayName(e) ?? e.Name, e.EntityType)),
            ReferenceKind.String => index.Strings.Entries.Values
                .Select(s => new PickerItem(s.Id, s.Value.Length > 90 ? s.Value[..90] + "…" : s.Value, "")),
            ReferenceKind.Brush => index.Brushes.Entries.Values.Select(b => new PickerItem(b.Name, b.Name, Path.GetFileName(b.File.RelativePath))),
            ReferenceKind.Sound => index.Sounds.Entries.Values.Select(s => new PickerItem(s.Name, s.Name, "")),
            ReferenceKind.Mesh => index.Meshes.Keys.Select(n => new PickerItem(n, n, "")),
            ReferenceKind.Particle => index.Particles.Keys.Select(n => new PickerItem(n, n, "")),
            ReferenceKind.Texture => index.Textures.Keys.Select(n => new PickerItem(n, n, "")),
            _ => [],
        };
        return items.OrderBy(i => i.Value, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
