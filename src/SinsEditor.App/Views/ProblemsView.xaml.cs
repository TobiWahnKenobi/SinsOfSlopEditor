using System.Windows.Controls;
using System.Windows.Input;
using SinsEditor.App.ViewModels;

namespace SinsEditor.App.Views;

public partial class ProblemsView : UserControl
{
    public ProblemsView() => InitializeComponent();

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ProblemsViewModel vm && sender is DataGrid { SelectedItem: ProblemRow row })
            vm.OpenCommand.Execute(row);
    }
}
