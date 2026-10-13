using System.Windows;
using System.Windows.Controls;

namespace SinsEditor.App.Editing;

/// <summary>Picks the editor template for a row: counters and sections first, then by value kind.</summary>
public sealed class PropertyRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Section { get; set; }
    public DataTemplate? Counter { get; set; }
    public DataTemplate? Bool { get; set; }
    public DataTemplate? Text { get; set; }
    public DataTemplate? Choice { get; set; }
    public DataTemplate? Reference { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        PropertyRowViewModel { IsCounter: true } => Counter,
        PropertyRowViewModel row => row.Kind switch
        {
            RowKind.Section => Section,
            RowKind.Bool => Bool,
            RowKind.Choice => Choice,
            RowKind.Reference => Reference,
            _ => Text,
        },
        _ => null,
    };
}
