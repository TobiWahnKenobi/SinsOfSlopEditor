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

    /// <summary>
    /// Mouse back/forward buttons. The middle button on a link (or a navigating button such as a sidebar entry) opens it in a new tab, with Shift in a
    /// new tab in the background.
    /// </summary>
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        if (e.ChangedButton == MouseButton.Middle && OpenInNewTab(e.OriginalSource as DependencyObject, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
        {
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.XButton1) { ViewModel.GoBackCommand.Execute(null); e.Handled = true; }
        if (e.ChangedButton == MouseButton.XButton2) { ViewModel.GoForwardCommand.Execute(null); e.Handled = true; }
    }

    private bool OpenInNewTab(DependencyObject? source, bool background)
    {
        for (var d = source; d != null; d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                 ? System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d)
                 : LogicalTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Documents.Hyperlink { Command: { } command } link)
            {
                if (!command.CanExecute(link.CommandParameter)) return false;
                ViewModel.FollowInNewTab(() => command.Execute(link.CommandParameter), background);
                return true;
            }
            // Buttons that navigate (sidebar entries, links styled as buttons); other buttons don't react to the middle button.
            if (d is System.Windows.Controls.Primitives.ButtonBase { Command: { } buttonCommand, CommandParameter: NavTarget target })
            {
                ViewModel.FollowInNewTab(() => buttonCommand.Execute(target), background);
                return true;
            }
            // Tab headers keep their own middle click (close).
            if (d is FrameworkElement { DataContext: DocumentTab }) return false;
        }
        return false;
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
        if (!await ViewModel.ResolveUnsavedChangesAsync("opening another mod")) return;
        if (((App)Application.Current).ShowOpenModDialog(this) is { } options) await ViewModel.OpenAsync(options);
    }

    private bool _closeConfirmed;

    /// <summary>Asks about unsaved changes; the window closes after they're saved or discarded.</summary>
    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || ViewModel.Workspace?.DirtySessions.Any() != true) return;
        e.Cancel = true;
        if (!await ViewModel.ResolveUnsavedChangesAsync("closing")) return;
        _closeConfirmed = true;
        Close();
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await ViewModel.ReloadAsync();
}
