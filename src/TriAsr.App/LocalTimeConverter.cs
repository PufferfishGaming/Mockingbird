using System.Globalization;
using System.Windows.Data;

namespace TriAsr.App;

/// <summary>Shows a moment (stored in UTC) as the person's own date and time, in the format of their regional settings.</summary>
public sealed class LocalTimeConverter : IValueConverter
{
    public static LocalTimeConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTimeOffset moment ? moment.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
