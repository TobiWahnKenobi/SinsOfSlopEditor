using System.Windows.Controls;
using System.Windows.Input;
using SinsEditor.App.ViewModels;

namespace SinsEditor.App.Views;

public partial class CategoryView : UserControl
{
    public CategoryView() => InitializeComponent();

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected(sender);

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OpenSelected(sender);
        e.Handled = true;
    }

    private void OpenSelected(object sender)
    {
        if (DataContext is CategoryViewModel vm && sender is DataGrid { SelectedItem: EntityRow row })
            vm.OpenCommand.Execute(row);
    }
}
