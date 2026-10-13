using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SinsEditor.App.Imaging;
using SinsEditor.App.Navigation;
using SinsEditor.Core;
using SinsEditor.Core.Index;

namespace SinsEditor.App.ViewModels;

/// <summary>
/// A node in the race tree. Entity nodes load their children (the entities they use) only when expanded,
/// because the reference graph is deep and has cycles.
/// </summary>
public sealed partial class RaceTreeNode : ObservableObject
{
    private static readonly RaceTreeNode Placeholder = new("…", null, null, null);
    private Func<IEnumerable<RaceTreeNode>>? _loadChildren;
    private readonly Func<BitmapSource?>? _icon;
    private BitmapSource? _iconValue;
    private bool _iconLoaded;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    public RaceTreeNode(string title, string? detail, EntityRecord? entity, Func<BitmapSource?>? icon,
        Func<IEnumerable<RaceTreeNode>>? loadChildren = null, bool isMissing = false)
    {
        Title = title;
        Detail = detail;
        Entity = entity;
        _icon = icon;
        IsMissing = isMissing;
        _loadChildren = loadChildren;
        if (loadChildren != null) Children.Add(Placeholder);
    }

    public string Title { get; }
    /// <summary>Muted text after the title: a count for sections, the referencing key for used entities.</summary>
    public string? Detail { get; }
    public EntityRecord? Entity { get; }
    public bool IsMissing { get; }
    public ObservableCollection<RaceTreeNode> Children { get; } = [];

    public BitmapSource? Icon
    {
        get
        {
            if (!_iconLoaded) { _iconValue = _icon?.Invoke(); _iconLoaded = true; }
            return _iconValue;
        }
    }

    public void Add(RaceTreeNode child) => Children.Add(child);

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value || _loadChildren is null) return;
        var load = _loadChildren;
        _loadChildren = null;
        Children.Clear();
        foreach (var child in load()) Children.Add(child);
    }
}

/// <summary>Race page: everything the race owns as a tree on the left, the selected item's details on the right.</summary>
public sealed partial class RaceViewModel : PageViewModel
{
    private static readonly (string Section, string Title)[] Sections =
    [
        ("capitalShipInfo", "Capital ships"),
        ("titanInfo", "Titans"),
        ("frigateInfo", "Frigates"),
        ("planetModuleInfo", "Planet modules"),
        ("starBaseInfo", "Starbases"),
        ("researchInfo", "Research"),
    ];

    private readonly ModWorkspace _workspace;
    private readonly Action<NavTarget> _navigate;

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    private RaceTreeNode? _selectedNode;

    [ObservableProperty]
    private EntityViewModel? _detail;

    public RaceViewModel(ModWorkspace workspace, EntityRecord race, Action<NavTarget> navigate)
    {
        _workspace = workspace;
        _navigate = navigate;
        Race = race;
        DisplayName = workspace.Index.DisplayName(race) ?? race.Name;
        BuildTree();
        SelectedNode = Roots[0];
    }

    public EntityRecord Race { get; }
    public string DisplayName { get; }
    public override string Title => DisplayName;
    public override string? Breadcrumb => "Races";
    public ObservableCollection<RaceTreeNode> Roots { get; } = [];

    partial void OnFilterTextChanged(string value) => BuildTree();

    partial void OnSelectedNodeChanged(RaceTreeNode? value)
    {
        Detail?.Detach();
        Detail = value?.Entity is { } entity ? new EntityViewModel(_workspace, entity, _navigate) : null;
    }

    partial void OnDetailChanged(EntityViewModel? oldValue, EntityViewModel? newValue)
    {
        if (oldValue != null) oldValue.PropertyChanged -= OnDetailPropertyChanged;
        if (newValue != null) newValue.PropertyChanged += OnDetailPropertyChanged;
        OnPropertyChanged(nameof(IsDirty));
    }

    private void OnDetailPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsDirty)) OnPropertyChanged(nameof(IsDirty));
    }

    /// <summary>Unsaved changes in the item shown on the right.</summary>
    public override bool IsDirty => Detail?.IsDirty ?? false;

    public override void Detach() => Detail?.Detach();

    [RelayCommand]
    private void Open(RaceTreeNode? node)
    {
        if (node?.Entity is { } entity) _navigate(new EntityTarget(entity.Name));
    }

    private void BuildTree()
    {
        var index = _workspace.Index;
        var items = index.ItemsOf(Race).ToList();
        bool filtering = FilterText.Length > 0;
        var root = new RaceTreeNode(DisplayName, Race.Name, Race, IconFor(Race)) { IsExpanded = true };

        foreach (var (section, title) in Sections)
        {
            var sectionItems = items.Where(i => i.Section == section && Matches(i)).ToList();
            if (sectionItems.Count == 0 && filtering) continue;
            var sectionNode = new RaceTreeNode(title, sectionItems.Count.ToString(), null, null) { IsExpanded = filtering || section != "researchInfo" };

            // Build pages (Page:0, Page:1, NotOnPage) or research fields become a level in between.
            var groups = section == "researchInfo"
                ? sectionItems.GroupBy(i => i.Entity is { } e ? Facets.TopLevelValues(e, "ResearchField").FirstOrDefault() ?? "Other" : "Missing")
                : sectionItems.GroupBy(i => PageTitle(i.Page));
            var groupList = groups.ToList();
            foreach (var group in groupList)
            {
                var parent = sectionNode;
                if (groupList.Count > 1 || group.Key.Length > 0)
                {
                    parent = new RaceTreeNode(group.Key.Length > 0 ? group.Key : title, group.Count().ToString(), null, null) { IsExpanded = filtering || section != "researchInfo" };
                    sectionNode.Add(parent);
                }
                foreach (var item in group.OrderBy(i => DisplayOf(i), StringComparer.CurrentCultureIgnoreCase))
                    parent.Add(ItemNode(item));
            }
            root.Add(sectionNode);
        }

        var problems = items.Where(i => i.Entity is null || _workspace.HasProblems(i.Entity)).ToList();
        if (problems.Count > 0 && !filtering)
        {
            var problemNode = new RaceTreeNode("Problems", problems.Count.ToString(), null, null);
            foreach (var item in problems.OrderBy(i => DisplayOf(i), StringComparer.CurrentCultureIgnoreCase)) problemNode.Add(ItemNode(item));
            root.Add(problemNode);
        }

        Roots.Clear();
        Roots.Add(root);
    }

    private RaceTreeNode ItemNode(RaceItem item) => item.Entity is { } entity
        ? EntityNode(entity, null, [Race.Name, entity.Name])
        : new RaceTreeNode(item.Name, "doesn't exist", null, null, isMissing: true);

    /// <summary>An entity and, when expanded, the entities it uses (skipping races and anything already on the path).</summary>
    private RaceTreeNode EntityNode(EntityRecord entity, string? via, HashSet<string> path) =>
        new(_workspace.Index.DisplayName(entity) ?? entity.Name, via, entity, IconFor(entity), () => UsedEntities(entity, path));

    private IEnumerable<RaceTreeNode> UsedEntities(EntityRecord entity, HashSet<string> path)
    {
        var index = _workspace.Index;
        foreach (var reference in index.OutgoingReferences(entity)
                     .Where(r => r.Kind == ReferenceKind.Entity)
                     .DistinctBy(r => r.Target, StringComparer.OrdinalIgnoreCase))
        {
            if (!reference.Resolved)
            {
                yield return new RaceTreeNode(reference.Target, reference.Key + " · doesn't exist", null, null, isMissing: true);
                continue;
            }
            var target = index.Entities[reference.Target];
            if (target.EntityType == "Player" || path.Contains(target.Name)) continue;
            yield return EntityNode(target, reference.Key, [.. path, target.Name]);
        }
    }

    private Func<BitmapSource?> IconFor(EntityRecord entity) =>
        () => ImageConverter.RenderBrush(_workspace.Textures, CategoryViewModel.HudIcon(entity));

    private string DisplayOf(RaceItem item) =>
        item.Entity is { } e ? _workspace.Index.DisplayName(e) ?? e.Name : item.Name;

    private bool Matches(RaceItem item) =>
        FilterText.Length == 0
        || DisplayOf(item).Contains(FilterText, StringComparison.CurrentCultureIgnoreCase)
        || item.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase);

    private static string PageTitle(string? page) => page switch
    {
        null => "",
        "NotOnPage" => "Not on a build page",
        _ when page.StartsWith("Page:") && int.TryParse(page[5..], out int n) => $"Build page {n + 1}",
        _ => page,
    };
}
