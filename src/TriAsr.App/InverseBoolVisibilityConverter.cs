using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TriAsr.App;

/// <summary>Visible while the value is false, collapsed while it is true.</summary>
public sealed class InverseBoolVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
