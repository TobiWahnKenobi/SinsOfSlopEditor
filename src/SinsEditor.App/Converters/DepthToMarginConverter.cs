using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SinsEditor.App.Converters;

/// <summary>Indents a row by its nesting depth.</summary>
public sealed class DepthToMarginConverter : IValueConverter
{
    public double Step { get; set; } = 18;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new Thickness(value is int depth ? depth * Step : 0, 0, 0, 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
