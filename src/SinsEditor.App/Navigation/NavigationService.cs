using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

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

/// <summary>Browser-style back/forward stacks plus a chronological history of every page shown.</summary>
public sealed partial class NavigationService : ObservableObject
{
    private readonly Stack<NavTarget> _back = new();
    private readonly Stack<NavTarget> _forward = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack), nameof(CanGoForward))]
    private NavTarget? _current;

    /// <summary>Newest first.</summary>
    public ObservableCollection<HistoryEntry> History { get; } = [];

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>Raised after <see cref="Current"/> changed; the handler shows the page and returns its title for the history.</summary>
    public Func<NavTarget, string>? Show { get; set; }

    public void Navigate(NavTarget target)
    {
        if (target == Current) return;
        if (Current != null) _back.Push(Current);
        _forward.Clear();
        MoveTo(target);
    }

    /// <summary>Forgets everything, e.g. when another mod is opened.</summary>
    public void Reset()
    {
        _back.Clear();
        _forward.Clear();
        History.Clear();
        Current = null;
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

    private void MoveTo(NavTarget target)
    {
        Current = target;
        string title = Show?.Invoke(target) ?? target.ToString();
        History.Insert(0, new HistoryEntry(target, title, DateTime.Now));
        if (History.Count > 500) History.RemoveAt(History.Count - 1);
    }
}
