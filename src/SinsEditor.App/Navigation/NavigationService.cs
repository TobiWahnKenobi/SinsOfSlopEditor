using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SinsEditor.App.ViewModels;

namespace SinsEditor.App.Navigation;

/// <summary>Where a page is. Records compare by value, so navigating to the current target does nothing.</summary>
public abstract record NavTarget;

public sealed record DashboardTarget : NavTarget;

public sealed record CategoryTarget(string CategoryId) : NavTarget;

public sealed record EntityTarget(string EntityName) : NavTarget;

public sealed record ProblemsTarget(string? Code = null) : NavTarget;

public sealed record HistoryEntry(NavTarget Target, string Title, DateTime VisitedAt)
{
    public string TimeText => VisitedAt.ToString("HH:mm:ss");
}

/// <summary>A visited page. The page object is kept so going back restores its state (filters, search, selection).</summary>
public sealed class NavEntry(NavTarget target)
{
    public NavTarget Target { get; } = target;
    public PageViewModel? Page { get; set; }
}

/// <summary>
/// Browser-style back/forward stacks for one tab. Every page shown is also added to a history shared by all tabs.
/// </summary>
public sealed partial class NavigationService(ObservableCollection<HistoryEntry> history, Func<NavTarget, PageViewModel?> createPage)
    : ObservableObject
{
    private readonly Stack<NavEntry> _back = new();
    private readonly Stack<NavEntry> _forward = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack), nameof(CanGoForward), nameof(CurrentTarget), nameof(CurrentPage))]
    private NavEntry? _current;

    public NavTarget? CurrentTarget => Current?.Target;
    public PageViewModel? CurrentPage => Current?.Page;
    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    public void Navigate(NavTarget target)
    {
        if (target == CurrentTarget) return;
        if (Current != null) _back.Push(Current);
        _forward.Clear();
        MoveTo(new NavEntry(target));
    }

    public void GoBack()
    {
        if (!CanGoBack) return;
        _forward.Push(Current!);
        MoveTo(_back.Pop());
    }

    public void GoForward()
    {
        if (!CanGoForward) return;
        _back.Push(Current!);
        MoveTo(_forward.Pop());
    }

    /// <summary>After the mod was reloaded: rebuild the current page now and the others when they're shown again.</summary>
    public void InvalidatePages()
    {
        InvalidateOtherPages();
        RecreateCurrentPage();
    }

    /// <summary>Drops the pages in the back and forward stacks; they're rebuilt when shown again.</summary>
    public void InvalidateOtherPages()
    {
        foreach (var entry in _back.Concat(_forward))
        {
            entry.Page?.Detach();
            entry.Page = null;
        }
    }

    public void RecreateCurrentPage()
    {
        if (Current is not { } current) return;
        current.Page?.Detach();
        current.Page = createPage(current.Target);
        OnPropertyChanged(nameof(CurrentPage));
    }

    /// <summary>Detaches every page, e.g. when the tab is closed.</summary>
    public void DetachAll()
    {
        foreach (var entry in _back.Concat(_forward).Append(Current))
            entry?.Page?.Detach();
    }

    private void MoveTo(NavEntry entry)
    {
        entry.Page ??= createPage(entry.Target);
        Current = entry;
        history.Insert(0, new HistoryEntry(entry.Target, entry.Page?.Title ?? entry.Target.ToString(), DateTime.Now));
        if (history.Count > 500) history.RemoveAt(history.Count - 1);
    }
}
