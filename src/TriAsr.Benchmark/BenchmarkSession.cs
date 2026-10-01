namespace TriAsr.Benchmark;

public sealed record Measurement(double Seconds, double? LoadSeconds = null, long? PeakRamBytes = null,
    long? PeakVramBytes = null, double? TokensPerSecond = null);
public sealed record BenchmarkRow(string Engine, string Model, string Backend, int Threads, string Strategy,
    double AudioSeconds, IReadOnlyList<Measurement> Runs, string? Error = null)
{
    public double? MedianSeconds => Error is null && Runs.Count >= 3 ? Median(Runs.Select(run => run.Seconds)) : null;
    public double? RealTimeFactor => Engine == "Correction" ? null : MedianSeconds / AudioSeconds;
    public string BackendLabel => Backend switch { "cpu" => "CPU", "vulkan" => "GPU (Vulkan)", "cuda" => "GPU (CUDA)", "rocm" => "GPU (ROCm/HIP)", "mixed" => "Best engine settings", _ => Backend };
    public string ThreadLabel => Threads > 0 ? Threads.ToString() : "—";
    public string Meaning => Error is not null ? "Failed · excluded from tuning"
        : MedianSeconds is not { } seconds ? "Measurement incomplete"
        : Engine == "Correction" ? $"{seconds:0.00} seconds per disagreement · model already loaded"
        : RealTimeFactor is > 0 ? $"{AudioSeconds:0.0}s audio processed in {seconds:0.00}s · {1 / RealTimeFactor:0.0}× real time"
        : "No timing available";
    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) throw new ArgumentException("No measured samples.");
        return sorted.Length % 2 == 0 ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2 : sorted[sorted.Length / 2];
    }
}
public sealed record ExecutionProfile(string Fingerprint, string WhisperBackend, int WhisperThreads,
    string CanaryBackend, int CanaryThreads, string CorrectionBackend, int CorrectionThreads, bool ParallelSpeech,
    DateTimeOffset MeasuredUtc, IReadOnlyList<BenchmarkRow> Results);
public static class BenchmarkSession
{
    public static async Task<BenchmarkRow> MeasureAsync(string engine, string model, string backend, int threads,
        string strategy, double audioSeconds, Func<int, CancellationToken, Task<Measurement>> run, CancellationToken token)
    {
        var measurements = new List<Measurement>();
        try
        {
            await run(-1, token); // Warmup is never ranked.
            for (var iteration = 0; iteration < 3; iteration++) measurements.Add(await run(iteration, token));
            return new(engine, model, backend, threads, strategy, audioSeconds, measurements);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { return new(engine, model, backend, threads, strategy, audioSeconds, measurements, error.Message); }
    }
}
