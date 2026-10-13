using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using SinsEditor.App.ViewModels;

namespace SinsEditor.App.Views;

public partial class CategoryView : UserControl
{
    private readonly List<DataGridColumn> _propertyColumns = [];

    public CategoryView() => InitializeComponent();

    /// <summary>Adds a column per detected property (e.g. "Frigate role type"), bound to the row's indexer.</summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        foreach (var column in _propertyColumns) Grid.Columns.Remove(column);
        _propertyColumns.Clear();
        if (e.NewValue is not CategoryViewModel vm) return;
        int insertAt = 3;
        foreach (var (id, title) in vm.PropertyColumns)
        {
            var column = new DataGridTextColumn
            {
                Header = title,
                Binding = new Binding($"[{id}]"),
                SortMemberPath = $"[{id}]",
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = 90,
            };
            _propertyColumns.Add(column);
            Grid.Columns.Insert(insertAt++, column);
        }
    }

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
