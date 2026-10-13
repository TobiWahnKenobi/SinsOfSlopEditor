using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SinsEditor.App.Navigation;
using SinsEditor.Core;
using SinsEditor.Core.Index;

namespace SinsEditor.App.ViewModels;

public sealed partial class NavItem(string title, NavTarget target, int? count, bool isProblems = false) : ObservableObject
{
    public string Title { get; } = title;
    public NavTarget Target { get; } = target;
    public string CountText { get; } = count is { } c ? (c >= 10_000 ? $"{c / 1000}k" : c.ToString("N0")) : "";
    public bool IsProblems { get; } = isProblems;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Main window state: loading, the open workspace, tabs, navigation and search.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private WorkspaceOptions? _options;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private ModWorkspace? _workspace;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand), nameof(GoForwardCommand), nameof(CloseTabCommand))]
    private DocumentTab? _selectedTab;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _loadingText = "";

    [ObservableProperty]
    private double _loadingProgress;

    [ObservableProperty]
    private string? _loadError;

    public ShellViewModel()
    {
        Search = new SearchViewModel(this);
    }

    public ObservableCollection<HistoryEntry> History { get; } = [];
    public ObservableCollection<DocumentTab> Tabs { get; } = [];
    public ObservableCollection<NavItem> NavItems { get; } = [];
    public SearchViewModel Search { get; }
    public PageViewModel? CurrentPage => SelectedTab?.Page;
    public string WindowTitle => Workspace is null ? "Sins Editor" : $"{Workspace.ModName} – Sins Editor";

    public async Task OpenAsync(WorkspaceOptions options)
    {
        bool otherMod = _options != null && !string.Equals(_options.EditedModPath, options.EditedModPath, StringComparison.OrdinalIgnoreCase);
        _options = options;
        IsLoading = true;
        LoadError = null;
        LoadingText = "Opening mod…";
        LoadingProgress = 0;
        var progress = new Progress<IndexProgress>(p =>
        {
            LoadingText = p.Total > 1 ? $"{p.Stage} ({p.Done:N0} of {p.Total:N0})" : p.Stage + "…";
            LoadingProgress = p.Total > 0 ? (double)p.Done / p.Total : 0;
        });
        try
        {
            Workspace = await Task.Run(() => ModWorkspace.OpenAsync(options, progress));
            Search.Reset();
            BuildNavItems();
            if (otherMod)
            {
                Tabs.Clear();
                History.Clear();
            }
            if (Tabs.Count == 0) OpenInNewTab(new DashboardTarget());
            else foreach (var tab in Tabs) tab.Navigation.InvalidatePages();
            OnPropertyChanged(nameof(CurrentPage));
            UpdateNavSelection();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            LoadError = $"Couldn't open the mod: {e.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public Task ReloadAsync() => _options is null ? Task.CompletedTask : OpenAsync(_options);

    /// <summary>Navigates the current tab, or opens a new tab when Ctrl is held.</summary>
    public void Navigate(NavTarget target)
    {
        if (SelectedTab is null || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) OpenInNewTab(target);
        else SelectedTab.Navigation.Navigate(target);
    }

    public void OpenInNewTab(NavTarget target)
    {
        var tab = new DocumentTab(History, CreatePage);
        tab.Navigation.PropertyChanged += OnTabNavigationChanged;
        Tabs.Add(tab);
        tab.Navigation.Navigate(target);
        SelectedTab = tab;
        CloseTabCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void NavigateTo(NavTarget? target)
    {
        if (target != null) Navigate(target);
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => SelectedTab?.Navigation.GoBack();

    private bool CanGoBack() => SelectedTab?.Navigation.CanGoBack == true;

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward() => SelectedTab?.Navigation.GoForward();

    private bool CanGoForward() => SelectedTab?.Navigation.CanGoForward == true;

    [RelayCommand]
    private void OpenHistory(HistoryEntry? entry)
    {
        if (entry != null) Navigate(entry.Target);
    }

    [RelayCommand]
    private void NewTab() => OpenInNewTab(new DashboardTarget());

    [RelayCommand(CanExecute = nameof(CanCloseTab))]
    private void CloseTab(DocumentTab? tab)
    {
        tab ??= SelectedTab;
        if (tab is null || Tabs.Count <= 1) return;
        int index = Tabs.IndexOf(tab);
        tab.Navigation.PropertyChanged -= OnTabNavigationChanged;
        Tabs.Remove(tab);
        if (SelectedTab == tab) SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        CloseTabCommand.NotifyCanExecuteChanged();
    }

    private bool CanCloseTab(DocumentTab? tab) => Tabs.Count > 1;

    [RelayCommand]
    private void SelectTab(DocumentTab? tab)
    {
        if (tab != null) SelectedTab = tab;
    }

    partial void OnSelectedTabChanged(DocumentTab? oldValue, DocumentTab? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null) newValue.IsSelected = true;
        UpdateNavSelection();
    }

    private void OnTabNavigationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NavigationService.Current) && e.PropertyName != nameof(NavigationService.CurrentPage)) return;
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
        if (sender == SelectedTab?.Navigation)
        {
            OnPropertyChanged(nameof(CurrentPage));
            UpdateNavSelection();
        }
    }

    private void BuildNavItems()
    {
        NavItems.Clear();
        var index = Workspace!.Index;
        NavItems.Add(new NavItem("Dashboard", new DashboardTarget(), null));
        foreach (var category in EntityCategories.All.Append(EntityCategories.Other))
        {
            int count = index.EntitiesIn(category).Count();
            if (count > 0) NavItems.Add(new NavItem(category.Name, new CategoryTarget(category.Id), count));
        }
        NavItems.Add(new NavItem("Problems", new ProblemsTarget(), Workspace.ProblemCount, isProblems: true));
    }

    private void UpdateNavSelection()
    {
        var target = SelectedTab?.Navigation.CurrentTarget;
        var page = SelectedTab?.Page;
        foreach (var item in NavItems)
        {
            item.IsSelected = item.Target == target
                || (page is EntityViewModel ev && item.Target == new CategoryTarget(ev.Category.Id))
                || (page is RaceViewModel && item.Target == new CategoryTarget(EntityCategories.Races.Id))
                || (target is ProblemsTarget && item.IsProblems);
        }
    }

    private PageViewModel? CreatePage(NavTarget target)
    {
        if (Workspace is not { } ws) return null;
        Action<NavTarget> navigate = Navigate;
        return target switch
        {
            DashboardTarget => new DashboardViewModel(ws, navigate, ReloadAsync),
            CategoryTarget c when EntityCategories.ById(c.CategoryId) is { } category => new CategoryViewModel(ws, category, navigate),
            EntityTarget e when ws.Index.Entities.TryGetValue(e.EntityName, out var entity) =>
                entity.EntityType == "Player" ? new RaceViewModel(ws, entity, navigate) : new EntityViewModel(ws, entity, navigate),
            ProblemsTarget p => new ProblemsViewModel(ws, p.Code, navigate),
            _ => new DashboardViewModel(ws, navigate, ReloadAsync),
        };
    }
}
