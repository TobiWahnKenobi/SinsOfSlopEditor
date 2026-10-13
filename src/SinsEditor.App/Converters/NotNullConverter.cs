using System.Globalization;
using System.Windows.Data;

namespace SinsEditor.App.Converters;

/// <summary>True when the value isn't null; used to disable links that have nowhere to go.</summary>
public sealed class NotNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
