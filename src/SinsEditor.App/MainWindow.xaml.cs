using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    private async void OpenMod_Click(object sender, RoutedEventArgs e)
    {
        if (((App)Application.Current).ShowOpenModDialog(this) is { } options) await ViewModel.OpenAsync(options);
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await ViewModel.ReloadAsync();
}
