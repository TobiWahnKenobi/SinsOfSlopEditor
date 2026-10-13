using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using SinsEditor.App.Editing;
using SinsEditor.App.Views;
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
        WeakReferenceMessenger.Default.Register<ShellViewModel, EntityOperationRequest>(this,
            static (shell, request) => _ = shell.RunEntityOperationAsync(request));
    }

    /// <summary>
    /// Duplicate/new, rename or delete: unsaved edits are saved or discarded first (the operation works on saved files),
    /// then the dialog shows the planned changes, and after applying the mod is reloaded and the result opened.
    /// </summary>
    public async Task RunEntityOperationAsync(EntityOperationRequest request)
    {
        if (Workspace is null || !Workspace.Index.Entities.TryGetValue(request.EntityName, out _)) return;
        string verb = request.Kind.ToString().ToLowerInvariant();
        if (!await ResolveUnsavedChangesAsync($"you {verb}")) return;
        if (_options is null || !Workspace.Index.Entities.TryGetValue(request.EntityName, out var entity)) return;

        var dialog = new EntityOperationViewModel(Workspace, request.Kind, entity);
        var window = new EntityOperationWindow(dialog) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() != true || dialog.Plan is not { } plan) return;

        try
        {
            plan.Apply(Workspace);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Couldn't apply the changes: {e.Message}\n\nSome files may already have been written. Reload to see the current state.",
                dialog.Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var category = EntityCategories.For(entity.EntityType);
        await OpenAsync(_options);
        Navigate(dialog.ResultEntity is { } name ? new EntityTarget(name) : new CategoryTarget(category.Id));
    }

    public ObservableCollection<HistoryEntry> History { get; } = [];
    public ObservableCollection<DocumentTab> Tabs { get; } = [];
    public ObservableCollection<NavItem> NavItems { get; } = [];
    public SearchViewModel Search { get; }
    public PageViewModel? CurrentPage => SelectedTab?.Page;
    public string WindowTitle => Workspace is null ? Branding.AppTitle : $"{Workspace.ModName} – {Branding.AppTitle}";

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
            if (Workspace != null) Workspace.Changed -= OnWorkspaceChanged;
            Workspace = await Task.Run(() => ModWorkspace.OpenAsync(options, progress));
            Workspace.Changed += OnWorkspaceChanged;
            Search.Reset();
            BuildNavItems();
            if (otherMod)
            {
                foreach (var tab in Tabs) tab.Navigation.DetachAll();
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

    public async Task ReloadAsync()
    {
        if (_options is null || !await ResolveUnsavedChangesAsync("reloading")) return;
        await OpenAsync(_options);
    }

    /// <summary>
    /// Asks what to do with unsaved changes before <paramref name="action"/> (e.g. "closing").
    /// Returns false if the user cancelled or a save failed.
    /// </summary>
    public async Task<bool> ResolveUnsavedChangesAsync(string action)
    {
        var dirty = Workspace?.DirtySessions.ToList() ?? [];
        if (dirty.Count == 0) return true;
        string names = string.Join("\n", dirty.Take(10).Select(s => "  " + s.EntityName)) + (dirty.Count > 10 ? $"\n  … and {dirty.Count - 10} more" : "");
        var answer = MessageBox.Show(
            $"{dirty.Count} entities have unsaved changes:\n\n{names}\n\nSave them before {action}?",
            "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.No) return true;
        return await SaveSessionsAsync(dirty);
    }

    private async Task<bool> SaveSessionsAsync(IReadOnlyList<Core.Editing.EntityEditSession> sessions)
    {
        foreach (var session in sessions)
        {
            try
            {
                await Workspace!.SaveAsync(session, session.Format ?? Workspace.DefaultSaveFormat(session.Original));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Core.Conversion.ConvertDataException)
            {
                MessageBox.Show($"Couldn't save {session.EntityName}: {e.Message}", "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }
        return true;
    }

    /// <summary>The entity editor in the current tab: an entity page, or the item shown on a race page.</summary>
    private EntityViewModel? CurrentEditor => CurrentPage switch
    {
        EntityViewModel e => e,
        RaceViewModel r => r.Detail,
        _ => null,
    };

    [RelayCommand]
    private async Task SaveCurrentAsync()
    {
        if (CurrentEditor is { } editor) await editor.SaveAsync();
    }

    [RelayCommand]
    private async Task SaveAllAsync()
    {
        CurrentEditor?.FlushRawText?.Invoke();
        if (Workspace != null) await SaveSessionsAsync(Workspace.DirtySessions.ToList());
    }

    [RelayCommand]
    private void UndoCurrent()
    {
        if (CurrentEditor is { CanUndo: true } editor) editor.Undo();
    }

    [RelayCommand]
    private void RedoCurrent()
    {
        if (CurrentEditor is { CanRedo: true } editor) editor.Redo();
    }

    /// <summary>
    /// After a save: counts and problems changed. Rebuild the sidebar and every page except the editors on screen,
    /// which refresh themselves and must keep their state.
    /// </summary>
    private void OnWorkspaceChanged()
    {
        BuildNavItems();
        foreach (var tab in Tabs)
        {
            tab.Navigation.InvalidateOtherPages();
            if (tab.Page is not (EntityViewModel or RaceViewModel or Research.ResearchViewModel)) tab.Navigation.RecreateCurrentPage();
        }
        UpdateNavSelection();
    }

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
        tab.Navigation.DetachAll();
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
        NavItems.Add(new NavItem("Brushes", new BrushListTarget(), index.Brushes.Entries.Count));
        NavItems.Add(new NavItem("Game log", new GameLogTarget(), null));
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
                || (page is RaceViewModel or Research.ResearchViewModel && item.Target == new CategoryTarget(EntityCategories.Races.Id))
                || (target is ProblemsTarget && item.IsProblems)
                || (target is BrushTarget && item.Target is BrushListTarget)
                || (target is GameLogTarget && item.Target is GameLogTarget);
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
            EntityTarget e when ws.Index.Entities.TryGetValue(e.EntityName, out var entity) => EntityPage(ws, entity, e.Line, navigate),
            ProblemsTarget p => new ProblemsViewModel(ws, p.Code, navigate),
            ResearchTarget r when ws.Index.Entities.TryGetValue(r.RaceName, out var race) => new Research.ResearchViewModel(ws, race, navigate),
            BrushListTarget => new BrushEditing.BrushListViewModel(ws, navigate),
            GameLogTarget g => new GameLogViewModel(ws, g.LogPath, navigate),
            BrushTarget b when b.BrushName is null || ws.Index.Brushes.Contains(b.BrushName) =>
                new BrushEditing.BrushEditorViewModel(ws, b.BrushName, navigate, ReloadAsync),
            _ => new DashboardViewModel(ws, navigate, ReloadAsync),
        };
    }

    private static PageViewModel EntityPage(ModWorkspace ws, EntityRecord entity, int? line, Action<NavTarget> navigate)
    {
        if (entity.EntityType == "Player")
        {
            var race = new RaceViewModel(ws, entity, navigate);
            race.Detail.PendingLine = line;
            return race;
        }
        return new EntityViewModel(ws, entity, navigate) { PendingLine = line };
    }
}
