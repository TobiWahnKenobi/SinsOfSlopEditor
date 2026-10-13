using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SinsEditor.App.ViewModels;

namespace SinsEditor.App.Views;

public partial class RaceView : UserControl
{
    public RaceView() => InitializeComponent();

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is RaceViewModel vm && e.NewValue is RaceTreeNode node) vm.SelectedNode = node;
    }

    /// <summary>Double-click opens the item as its own page (Ctrl+double-click in a new tab).</summary>
    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is RaceViewModel vm && Tree.SelectedItem is RaceTreeNode { Entity: not null } node && node.Entity != vm.Race)
        {
            vm.OpenCommand.Execute(node);
            e.Handled = true;
        }
    }
}
