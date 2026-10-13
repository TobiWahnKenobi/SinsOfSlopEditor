using System.Windows.Controls;
using System.Windows.Input;
using SinsEditor.App.ViewModels;

namespace SinsEditor.App.Views;

public partial class GameLogView : UserControl
{
    public GameLogView() => InitializeComponent();

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is GameLogViewModel vm && sender is DataGrid { SelectedItem: LogRow row }) vm.OpenCommand.Execute(row);
    }
}
