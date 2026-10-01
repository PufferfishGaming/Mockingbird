using System.Text.Json;
using TriAsr.Benchmark;

namespace TriAsr.Ui.SmokeTests;

public sealed class BenchmarkMetricsTests
{
    private static Measurement Run(double seconds, double? cpu, long? ram) => new(seconds, null, ram, null, null, cpu);

    [Fact]
    public void ResultsShowProcessorTimeAndPeakMemoryNextToSpeed()
    {
        var row = new BenchmarkRow("Whisper", "model", "cpu", 8, "single", 8, [Run(4, 12, 2_000_000_000), Run(5, 14, 3_000_000_000), Run(3, 10, 2_500_000_000)]);
        Assert.Equal(12, row.MedianCpuSeconds);
        Assert.Equal(3_000_000_000, row.PeakRamBytes);
        Assert.Contains("real time", row.Meaning);
        Assert.Contains("CPU time", row.Meaning);
        Assert.Contains("peak RAM", row.Meaning);
    }

    [Fact]
    public void RowsMeasuredBeforeThisFeatureStillShowTheirSpeedAndNothingInvented()
    {
        var row = new BenchmarkRow("Whisper", "model", "cpu", 8, "single", 8, [new(4), new(5), new(3)]);
        Assert.Null(row.MedianCpuSeconds);
        Assert.Null(row.PeakRamBytes);
        Assert.Contains("real time", row.Meaning);
        Assert.DoesNotContain("CPU time", row.Meaning);
        Assert.DoesNotContain("peak RAM", row.Meaning);
    }

    [Fact]
    public void AMissingCpuReadingInOneRunMeansNoCpuFigureRatherThanAWrongOne()
    {
        var row = new BenchmarkRow("Canary", "model", "cpu", 8, "single", 8, [Run(4, 12, null), Run(5, null, null), Run(3, 10, null)]);
        Assert.Null(row.MedianCpuSeconds);
        Assert.DoesNotContain("CPU time", row.Meaning);
    }

    [Fact]
    public void FailedRowsAndTheCombinedStrategyRowCarryNoResourceNote()
    {
        var failed = new BenchmarkRow("Whisper", "model", "cpu", 8, "single", 8, [], "boom");
        Assert.Contains("excluded", failed.Meaning);
        Assert.DoesNotContain("CPU time", failed.Meaning);
        var combined = new BenchmarkRow("Dual ASR", "selected models", "mixed", 0, "parallel", 8, [Run(4, 12, 1), Run(5, 14, 1), Run(3, 10, 1)]);
        Assert.DoesNotContain("CPU time", combined.Meaning);
    }

    [Fact]
    public void SavedTuningResultsFromEarlierVersionsStillLoad()
    {
        const string legacy = """
            {"Engine":"Whisper","Model":"m","Backend":"cpu","Threads":8,"Strategy":"single","AudioSeconds":8,
             "Runs":[{"Seconds":4,"PeakRamBytes":2000000000},{"Seconds":5},{"Seconds":3}],"Error":null}
            """;
        var row = JsonSerializer.Deserialize<BenchmarkRow>(legacy)!;
        Assert.Equal(4, row.MedianSeconds);
        Assert.Null(row.MedianCpuSeconds);
        Assert.Equal(2_000_000_000, row.PeakRamBytes);
    }
}
