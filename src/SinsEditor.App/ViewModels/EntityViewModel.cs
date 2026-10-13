using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using SinsEditor.App.Imaging;
using SinsEditor.App.Navigation;
using SinsEditor.Core;
using SinsEditor.Core.Index;

namespace SinsEditor.App.ViewModels;

public sealed record ReferenceRow(string Key, string Kind, string Target, string? TargetDisplayName, bool Resolved, EntityTarget? NavTarget, int Line)
{
    public string Text => TargetDisplayName is null || TargetDisplayName == Target ? Target : $"{TargetDisplayName}  ({Target})";
}

public sealed record ReferenceGroup(string Title, IReadOnlyList<ReferenceRow> Rows);

/// <summary>Read-only entity page for M2: header, connected items both ways, and the raw text. Editing arrives in M4.</summary>
public sealed partial class EntityViewModel : PageViewModel
{
    private readonly Action<NavTarget> _navigate;

    public EntityViewModel(ModWorkspace workspace, EntityRecord entity, Action<NavTarget> navigate)
    {
        _navigate = navigate;
        var index = workspace.Index;
        Entity = entity;
        DisplayName = index.DisplayName(entity) ?? entity.Name;
        Category = EntityCategories.For(entity.EntityType);
        Icon = ImageConverter.RenderBrush(workspace.Textures, CategoryViewModel.HudIcon(entity));

        string layer = entity.File.Layer.Name;
        LayerText = entity.File.IsOverride
            ? $"{layer}, overrides {string.Join(", ", entity.File.OverriddenLayers.Select(l => l.Name))}"
            : layer;
        if (!entity.InManifest) LayerText += " · not in entity.manifest";

        Races = index.RacesOwning(entity.Name)
            .Select(r => new ReferenceRow("", "Race", r.Name, index.DisplayName(r), true, new EntityTarget(r.Name), -1))
            .ToList();

        Uses = index.OutgoingReferences(entity)
            .GroupBy(r => r.Kind)
            .OrderBy(g => g.Key)
            .Select(g => new ReferenceGroup(GroupTitle(g.Key, g.Count()), g
                .Select(r => new ReferenceRow(r.Key, r.Kind.ToString(), r.Target, TargetName(index, r.Kind, r.Target), r.Resolved,
                    r.Kind == ReferenceKind.Entity && r.Resolved ? new EntityTarget(r.Target) : null, r.LineIndex + 1))
                .ToList()))
            .ToList();

        UsedBy = index.IncomingReferences(ReferenceKind.Entity, entity.Name)
            .GroupBy(r => EntityCategories.For(r.Source.EntityType).Name)
            .OrderBy(g => g.Key)
            .Select(g => new ReferenceGroup($"{g.Key} · {g.Count()}", g
                .Select(r => new ReferenceRow(r.Key, r.Source.EntityType, r.Source.Name, index.DisplayName(r.Source), true,
                    new EntityTarget(r.Source.Name), r.LineIndex + 1))
                .ToList()))
            .ToList();

        RawText = string.Join(Environment.NewLine, entity.Document.Lines.Select(l => l.Text));
    }

    public EntityRecord Entity { get; }
    public EntityCategory Category { get; }
    public override string Title => DisplayName;
    public override string? Breadcrumb => Category.Name;

    public string DisplayName { get; }
    public string Subtitle => $"{Entity.Name} · {Entity.EntityType} · {Entity.File.RelativePath}";
    public string LayerText { get; }
    public BitmapSource? Icon { get; }
    public IReadOnlyList<ReferenceRow> Races { get; }
    public IReadOnlyList<ReferenceGroup> Uses { get; }
    public IReadOnlyList<ReferenceGroup> UsedBy { get; }
    public string RawText { get; }
    public bool HasNoUsedBy => UsedBy.Count == 0;

    [RelayCommand]
    private void Open(ReferenceRow? row)
    {
        if (row?.NavTarget != null) _navigate(row.NavTarget);
    }

    private static string GroupTitle(ReferenceKind kind, int count) => kind switch
    {
        ReferenceKind.Entity => "Entities",
        ReferenceKind.String => "Strings",
        ReferenceKind.Brush => "Brushes",
        ReferenceKind.Mesh => "Meshes",
        ReferenceKind.Particle => "Particles",
        ReferenceKind.Texture => "Textures",
        ReferenceKind.Sound => "Sounds",
        _ => kind.ToString(),
    } + $" · {count}";

    private static string? TargetName(ModIndex index, ReferenceKind kind, string target) => kind switch
    {
        ReferenceKind.Entity when index.Entities.TryGetValue(target, out var e) => index.DisplayName(e),
        ReferenceKind.String => index.Strings.Get(target) is { } s ? $"“{Truncate(s, 60)}”" : null,
        _ => null,
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
