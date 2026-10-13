using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SinsEditor.App.Navigation;

namespace SinsEditor.App.ViewModels;

/// <summary>A tab in the main area with its own back/forward navigation.</summary>
public sealed partial class DocumentTab : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public DocumentTab(ObservableCollection<HistoryEntry> history, Func<NavTarget, PageViewModel?> createPage)
    {
        Navigation = new NavigationService(history, createPage);
        Navigation.PropertyChanged += OnNavigationChanged;
    }

    private PageViewModel? _observedPage;

    public NavigationService Navigation { get; }
    public PageViewModel? Page => Navigation.CurrentPage;
    public string Title => Page?.Title ?? "…";
    public bool IsDirty => Page?.IsDirty ?? false;

    private void OnNavigationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NavigationService.CurrentPage) or nameof(NavigationService.Current))
        {
            if (_observedPage != null) _observedPage.PropertyChanged -= OnPageChanged;
            _observedPage = Page;
            if (_observedPage != null) _observedPage.PropertyChanged += OnPageChanged;
            OnPropertyChanged(nameof(Page));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(IsDirty));
        }
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PageViewModel.IsDirty) or nameof(PageViewModel.Title))
        {
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(Title));
        }
    }
}
