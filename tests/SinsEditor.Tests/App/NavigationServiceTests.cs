using System.Collections.ObjectModel;
using SinsEditor.App.Navigation;
using SinsEditor.App.ViewModels;

namespace SinsEditor.Tests.App;

public class NavigationServiceTests
{
    private sealed class TestPage(string title) : PageViewModel
    {
        public override string Title => title;
    }

    private readonly ObservableCollection<HistoryEntry> _history = [];
    private int _created;

    private NavigationService Create() => new(_history, t =>
    {
        _created++;
        return new TestPage(t.ToString());
    });

    [Fact]
    public void Back_and_forward_move_between_pages()
    {
        var nav = Create();
        nav.Navigate(new DashboardTarget());
        nav.Navigate(new CategoryTarget("frigates"));
        nav.Navigate(new EntityTarget("A"));

        nav.GoBack();
        Assert.Equal(new CategoryTarget("frigates"), nav.CurrentTarget);
        nav.GoBack();
        Assert.Equal(new DashboardTarget(), nav.CurrentTarget);
        Assert.False(nav.CanGoBack);
        nav.GoForward();
        Assert.Equal(new CategoryTarget("frigates"), nav.CurrentTarget);
        Assert.True(nav.CanGoForward);
    }

    [Fact]
    public void Navigating_after_going_back_drops_the_forward_stack()
    {
        var nav = Create();
        nav.Navigate(new DashboardTarget());
        nav.Navigate(new EntityTarget("A"));
        nav.GoBack();

        nav.Navigate(new EntityTarget("B"));

        Assert.False(nav.CanGoForward);
    }

    [Fact]
    public void Navigating_to_the_current_page_does_nothing()
    {
        var nav = Create();
        nav.Navigate(new EntityTarget("A"));

        nav.Navigate(new EntityTarget("A"));

        Assert.False(nav.CanGoBack);
        Assert.Single(_history);
    }

    [Fact]
    public void Going_back_restores_the_same_page_object()
    {
        var nav = Create();
        nav.Navigate(new CategoryTarget("frigates"));
        var page = nav.CurrentPage;
        nav.Navigate(new EntityTarget("A"));

        nav.GoBack();

        Assert.Same(page, nav.CurrentPage);
        Assert.Equal(2, _created);
    }

    [Fact]
    public void History_records_every_visit_newest_first()
    {
        var nav = Create();
        nav.Navigate(new DashboardTarget());
        nav.Navigate(new EntityTarget("A"));
        nav.GoBack();

        Assert.Equal([new DashboardTarget(), new EntityTarget("A"), new DashboardTarget()], _history.Select(h => h.Target));
    }

    [Fact]
    public void InvalidatePages_rebuilds_current_now_and_others_later()
    {
        var nav = Create();
        nav.Navigate(new DashboardTarget());
        nav.Navigate(new EntityTarget("A"));
        var old = nav.CurrentPage;

        nav.InvalidatePages();
        Assert.NotSame(old, nav.CurrentPage);
        Assert.Equal(3, _created);

        nav.GoBack();
        Assert.Equal(4, _created);
    }
}
