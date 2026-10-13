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

    public NavigationService Navigation { get; }
    public PageViewModel? Page => Navigation.CurrentPage;
    public string Title => Page?.Title ?? "…";

    private void OnNavigationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NavigationService.CurrentPage) or nameof(NavigationService.Current))
        {
            OnPropertyChanged(nameof(Page));
            OnPropertyChanged(nameof(Title));
        }
    }
}
