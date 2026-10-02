using System.Globalization;
using System.Windows.Data;
using TriAsr.Benchmark;

namespace TriAsr.App;

/// <summary>The sentences of the benchmark table in the interface language. The same facts as <see cref="BenchmarkRow.Meaning"/>, which stays English for logs and tests.</summary>
public static class BenchmarkText
{
    public static string Meaning(BenchmarkRow row) => Speed(row) + ResourceNote(row);

    public static string Backend(BenchmarkRow row) => Loc.T(row.BackendLabel);

    private static string Speed(BenchmarkRow row) => row.Error is not null ? Loc.T("Failed · excluded from tuning")
        : row.MedianSeconds is not { } seconds ? Loc.T("Measurement incomplete")
        : row.Engine == "Correction" ? Loc.T("{0:0.00} seconds per disagreement · model already loaded", seconds)
        : row.RealTimeFactor is > 0 ? Loc.T("{0:0.0} s of audio processed in {1:0.00} s · speed {2:0.0}× real time", row.AudioSeconds, seconds, 1 / row.RealTimeFactor)
        : Loc.T("No timing available");

    private static string ResourceNote(BenchmarkRow row)
    {
        if (row.Error is not null || row.Engine == "Dual ASR") return "";
        var parts = new List<string>();
        if (row.MedianCpuSeconds is { } cpu) parts.Add(Loc.T("CPU time {0:0.0} s", cpu));
        if (row.PeakRamBytes is { } peak) parts.Add(Loc.T("peak RAM use: {0:N0} MiB", peak / 1048576d));
        return parts.Count == 0 ? "" : " · " + string.Join(" · ", parts);
    }
}

/// <summary>Shows a benchmark row's explanation, or its backend, in the interface language (a MultiBinding with <see cref="Loc.Version"/>).</summary>
public sealed class BenchmarkTextConverter(bool backend) : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length > 0 && values[0] is BenchmarkRow row ? backend ? BenchmarkText.Backend(row) : BenchmarkText.Meaning(row) : "";

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
