using System.Globalization;
using System.Windows.Data;

namespace SinsEditor.App.Converters;

/// <summary>Binds radio buttons to an enum: true when the value equals the parameter; checking sets it.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(targetType, name) : Binding.DoNothing;
}
