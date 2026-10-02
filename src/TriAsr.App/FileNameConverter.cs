using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace TriAsr.App;

/// <summary>Shows a path as the name of the file only.</summary>
public sealed class FileNameConverter : IValueConverter
{
    public static FileNameConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is string path ? Path.GetFileName(path) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
