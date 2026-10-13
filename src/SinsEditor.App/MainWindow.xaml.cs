using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SinsEditor.App.Navigation;
using SinsEditor.App.ViewModels;

namespace SinsEditor.App;

public partial class MainWindow : Window
{
    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ViewModel = viewModel;
    }

    public ShellViewModel ViewModel { get; }

    /// <summary>Mouse back/forward buttons.</summary>
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (e.ChangedButton == MouseButton.XButton1) { ViewModel.GoBackCommand.Execute(null); e.Handled = true; }
        if (e.ChangedButton == MouseButton.XButton2) { ViewModel.GoForwardCommand.Execute(null); e.Handled = true; }
    }

    private void HistoryList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { SelectedItem: HistoryEntry entry } list)
        {
            HistoryToggle.IsChecked = false;
            list.SelectedItem = null;
            ViewModel.OpenHistoryCommand.Execute(entry);
        }
    }

    private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DocumentTab tab }) ViewModel.SelectTabCommand.Execute(tab);
    }

    /// <summary>Middle click closes a tab, like in browsers.</summary>
    private void Tab_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is FrameworkElement { DataContext: DocumentTab tab })
            ViewModel.CloseTabCommand.Execute(tab);
    }

    private void SearchOverlay_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            Dispatcher.BeginInvoke(() => { SearchBox.Focus(); SearchBox.SelectAll(); }, DispatcherPriority.Input);
    }

    private void SearchOverlay_MouseDown(object sender, MouseButtonEventArgs e) => ViewModel.Search.Close();

    /// <summary>Keeps clicks inside the panel from closing it.</summary>
    private void SearchPanel_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var search = ViewModel.Search;
        switch (e.Key)
        {
            case Key.Down: search.MoveSelection(1); break;
            case Key.Up: search.MoveSelection(-1); break;
            case Key.PageDown: search.MoveSelection(8); break;
            case Key.PageUp: search.MoveSelection(-8); break;
            case Key.Enter: search.Accept(null); break;
            case Key.Escape: search.Close(); break;
            default: return;
        }
        if (search.Selected != null) SearchResults.ScrollIntoView(search.Selected);
        e.Handled = true;
    }

    private void SearchResults_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && SearchResults.SelectedItem is SearchResult result) ViewModel.Search.Accept(result);
    }

    private async void OpenMod_Click(object sender, RoutedEventArgs e)
    {
        if (((App)Application.Current).ShowOpenModDialog(this) is { } options) await ViewModel.OpenAsync(options);
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await ViewModel.ReloadAsync();
}
