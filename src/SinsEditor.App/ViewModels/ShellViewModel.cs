using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SinsEditor.App.Navigation;
using SinsEditor.Core;
using SinsEditor.Core.Index;
using SinsEditor.Core.Validation;

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

/// <summary>Main window state: loading, the open workspace, navigation and the current page.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private WorkspaceOptions? _options;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private ModWorkspace? _workspace;

    [ObservableProperty]
    private PageViewModel? _currentPage;

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
        Navigation.Show = ShowPage;
        Navigation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NavigationService.Current))
            {
                GoBackCommand.NotifyCanExecuteChanged();
                GoForwardCommand.NotifyCanExecuteChanged();
            }
        };
    }

    public NavigationService Navigation { get; } = new();
    public ObservableCollection<NavItem> NavItems { get; } = [];
    public string WindowTitle => Workspace is null ? "Sins Editor" : $"{Workspace.ModName} – Sins Editor";

    public async Task OpenAsync(WorkspaceOptions options)
    {
        if (_options != null && !string.Equals(_options.EditedModPath, options.EditedModPath, StringComparison.OrdinalIgnoreCase))
            Navigation.Reset();
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
            BuildNavItems();
            var target = Navigation.Current ?? new DashboardTarget();
            // Re-show the current page after a reload so it reflects the new data.
            if (Navigation.Current is null) Navigation.Navigate(target);
            else ShowPage(target);
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

    [RelayCommand]
    private void Navigate(NavTarget? target)
    {
        if (target != null) Navigation.Navigate(target);
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => Navigation.GoBack();

    private bool CanGoBack() => Navigation.CanGoBack;

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward() => Navigation.GoForward();

    private bool CanGoForward() => Navigation.CanGoForward;

    [RelayCommand]
    private void OpenHistory(HistoryEntry? entry)
    {
        if (entry != null) Navigation.Navigate(entry.Target);
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

    private string ShowPage(NavTarget target)
    {
        CurrentPage = CreatePage(target);
        foreach (var item in NavItems)
        {
            item.IsSelected = item.Target == target
                || (target is EntityTarget e && CurrentPage is EntityViewModel ev && item.Target == new CategoryTarget(ev.Category.Id))
                || (target is ProblemsTarget && item.IsProblems);
        }
        return CurrentPage?.Title ?? target.ToString();
    }

    private PageViewModel? CreatePage(NavTarget target)
    {
        if (Workspace is not { } ws) return null;
        Action<NavTarget> navigate = Navigation.Navigate;
        return target switch
        {
            DashboardTarget => new DashboardViewModel(ws, navigate, ReloadAsync),
            CategoryTarget c when EntityCategories.ById(c.CategoryId) is { } category => new CategoryViewModel(ws, category, navigate),
            EntityTarget e when ws.Index.Entities.TryGetValue(e.EntityName, out var entity) => new EntityViewModel(ws, entity, navigate),
            ProblemsTarget p => new ProblemsViewModel(ws, p.Code, navigate),
            _ => new DashboardViewModel(ws, navigate, ReloadAsync),
        };
    }
}
