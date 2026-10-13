using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SinsEditor.App.Assets;

namespace SinsEditor.App.Views;

public partial class AssetListView : UserControl
{
    public AssetListView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
            PreviewColumn.Visibility = DataContext is AssetListViewModel { ShowPreview: true } ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is AssetListViewModel vm && sender is DataGrid { SelectedItem: AssetRow row }) vm.OpenCommand.Execute(row);
    }
}
