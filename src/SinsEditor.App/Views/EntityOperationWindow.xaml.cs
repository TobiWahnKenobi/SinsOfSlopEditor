using System.Windows;
using SinsEditor.App.Editing;

namespace SinsEditor.App.Views;

public partial class EntityOperationWindow : Window
{
    public EntityOperationWindow(EntityOperationViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += ok => DialogResult = ok;
    }
}
