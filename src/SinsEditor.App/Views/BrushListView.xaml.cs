using System.Windows.Controls;
using System.Windows.Input;
using SinsEditor.App.BrushEditing;

namespace SinsEditor.App.Views;

public partial class BrushListView : UserControl
{
    public BrushListView() => InitializeComponent();

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is BrushListViewModel vm && sender is DataGrid { SelectedItem: BrushRow row }) vm.OpenCommand.Execute(row);
    }
}
