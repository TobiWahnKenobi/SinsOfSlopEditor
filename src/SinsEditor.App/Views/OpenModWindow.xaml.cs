using System.Windows;
using SinsEditor.App.ViewModels;

namespace SinsEditor.App.Views;

public partial class OpenModWindow : Window
{
    public OpenModWindow(OpenModViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += ok => DialogResult = ok;
    }
}
