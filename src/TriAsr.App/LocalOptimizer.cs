using System.Diagnostics;
using System.IO;
using System.Text.Json;
using TriAsr.Application;
using TriAsr.Audio;
using TriAsr.Benchmark;
using TriAsr.Engine.Canary;
using TriAsr.Engine.Llm;
using TriAsr.Engine.Whisper;
using TriAsr.Hardware;
using TriAsr.Infrastructure;

namespace TriAsr.App;

public sealed class LocalOptimizer(RuntimePaths paths, IStoragePaths storage, IProcessRunner runner, IAudioNormalizer normalizer, ModelStore models, IRecordRepository records, ResourceGovernor governor)
{
    public async Task<ExecutionProfile> OptimizeAsync(string source, HardwareProfile hardware, IProgress<string>? progress, CancellationToken token, IProgress<BenchmarkRow>? measurements = null)
    {
        var fingerprint = paths.ConfigurationFingerprint(hardware.Fingerprint);
        var directory = Path.Combine(storage.Root, "Benchmarks", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        // The correction model is optional (Settings, off by default): it is measured only when it is installed.
        var correctionInstalled = File.Exists(paths.CorrectionModel);
        foreach (var path in correctionInstalled ? new[] { paths.WhisperModel, paths.CanaryModel, paths.CorrectionModel } : [paths.WhisperModel, paths.CanaryModel])
        {
            var entry = ModelManifest.Entries.First(item => models.PathFor(item) == path);
            progress?.Report(Loc.T("Verifying {0}", entry.Name));
            if (!await models.VerifyCachedAsync(entry, token)) throw new InvalidDataException(Loc.T("Model checksum failed: {0} {1}", entry.Name, entry.Quantization));
        }
        var normalized = Path.Combine(directory, "normalized.wav");
        await normalizer.NormalizeAsync(source, normalized, token);
        var sample = Path.Combine(directory, "sample.wav");
        var cut = await runner.RunAsync(new(paths.Ffmpeg, ["-nostdin", "-v", "error", "-n", "-threads", "2", "-i", normalized, "-t", "8", "-c:a", "pcm_s16le", sample], directory, TimeSpan.FromMinutes(1)), token);
        if (cut.ExitCode != 0) throw new InvalidOperationException(Loc.T("Benchmark sample extraction failed."));
        var seconds = WaveAudio.Inspect(sample).DurationSeconds;
        if (seconds < 3) throw new InvalidDataException(Loc.T("Choose at least three seconds of speech for optimization."));
        var budget = governor.For(ResourceProfile.Default).Threads; // tuning never tests more threads than a normal job may use
        var defaultThreads = Math.Clamp(hardware.Topology.PerformanceCores ?? hardware.Topology.LogicalProcessors / 2, 1, Math.Min(12, budget));
        var vram = hardware.Gpus.MaxBy(gpu => gpu.DedicatedBytes)?.DedicatedBytes ?? 0;
        var gpuAllowed = GpuMemoryPlanner.Fits(vram, (ulong)Math.Max(new FileInfo(paths.WhisperModel).Length, correctionInstalled ? new FileInfo(paths.CorrectionModel).Length : 0), 1024UL * 1024 * 1024, 512UL * 1024 * 1024, 1024UL * 1024 * 1024);
        var backends = new[] { "Whisper", "Canary", "Correction" }.SelectMany(engine => BackendRuntimes.Candidates(paths, hardware, engine))
            .Where(backend => backend == "cpu" || gpuAllowed).Distinct().OrderBy(backend => backend == "cpu" ? 1 : 0).ToArray();
        var detectionBackend = BackendRuntimes.Candidates(paths, hardware, "Whisper").Where(backend => backend == "cpu" || gpuAllowed).OrderBy(backend => backend == "cpu" ? 1 : 0).First();
        WhisperEngine.LanguageDetection language;
        try { language = await new WhisperEngine(runner, paths.WhisperFor(detectionBackend), paths.WhisperModel, defaultThreads)
            .DetectLanguageAsync(sample, seconds, Path.Combine(directory, "language"), detectionBackend, paths.Ffmpeg, token); }
        catch (Exception error) when (error is not OperationCanceledException && detectionBackend != "cpu")
        {
            progress?.Report(Loc.T("GPU language check failed; retrying on CPU. GPU benchmark candidates will still be tested independently."));
            language = await new WhisperEngine(runner, paths.Whisper, paths.WhisperModel, defaultThreads)
                .DetectLanguageAsync(sample, seconds, Path.Combine(directory, "language-cpu"), "cpu", paths.Ffmpeg, token);
        }
        if (!TriAsr.Domain.LanguageCatalog.CanaryCodes.Contains(language.Language)) throw new InvalidDataException(Loc.T("Choose a tuning recording in one of Canary’s 25 languages to measure both engines. Transcription still supports Whisper’s full language catalog."));
        var results = new List<BenchmarkRow>();
        var runId = 0;
        async Task<Measurement> Whisper(string backend, int threads, CancellationToken ct)
        {
            var result = await new WhisperEngine(runner, paths.WhisperFor(backend), paths.WhisperModel, threads).TranscribeAsync(sample, seconds, language.Language,
                Path.Combine(directory, $"whisper-{Interlocked.Increment(ref runId)}"), backend, ct);
            return new(result.InferenceSeconds, result.LoadSeconds, result.PeakRamBytes, CpuSeconds: result.CpuSeconds);
        }
        async Task<Measurement> Canary(string backend, int threads, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref runId);
            var output = Path.Combine(directory, $"canary-{id}.json"); var request = Path.Combine(directory, $"canary-{id}-request.json");
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new CanaryRequest(paths.CanaryFor(backend), paths.CanaryModel, sample, language.Language, backend, threads, output)), ct);
            var run = await runner.RunAsync(new(paths.CanaryWorker, ["--canary", request], Path.GetDirectoryName(paths.CanaryWorker)!, TimeSpan.FromMinutes(5)), ct);
            await File.WriteAllTextAsync(Path.Combine(directory, $"canary-{id}.stderr.txt"), run.StandardError, ct);
            if (run.ExitCode != 0) throw new InvalidOperationException(Loc.T("Canary benchmark failed: {0}", run.StandardError[^Math.Min(run.StandardError.Length, 500)..]));
            var native = JsonSerializer.Deserialize<CanaryNative.Result>(await File.ReadAllTextAsync(output, ct))!;
            if (native.Transcript.ActualBackend != backend) throw new InvalidDataException(Loc.T("Canary benchmark backend mismatch."));
            return new(run.Seconds, native.Transcript.LoadSeconds, run.PeakRamBytes, CpuSeconds: run.CpuSeconds);
        }
        foreach (var backend in backends)
        {
            var threads = backend == "cpu" ? new[] { Math.Min(4, hardware.Topology.LogicalProcessors), defaultThreads, Math.Min(Math.Min(24, hardware.Topology.LogicalProcessors), budget) }.Select(value => Math.Max(1, value)).Distinct().Order().ToArray() : [defaultThreads];
            foreach (var count in threads)
            {
                foreach (var engine in new[] { "Whisper", "Canary" })
                {
                    if (!BackendRuntimes.Candidates(paths, hardware, engine).Contains(backend)) continue;
                    progress?.Report(Loc.T("{0} · {1} · threads: {2} · warmup + 3 measurements", Loc.T(engine), backend, count));
                    var row = await BenchmarkSession.MeasureAsync(engine, engine == "Whisper" ? Path.GetFileName(paths.WhisperModel) : Path.GetFileName(paths.CanaryModel), backend, count, "single", seconds,
                        (_, ct) => engine == "Whisper" ? Whisper(backend, count, ct) : Canary(backend, count, ct), token);
                    results.Add(row); measurements?.Report(row); await SaveResults(directory, results, token);
                }
            }
        }
        var whisperBest = results.Where(row => row.Engine == "Whisper" && row.MedianSeconds.HasValue).MinBy(row => row.MedianSeconds)
            ?? throw new InvalidOperationException(Loc.T("No Whisper configuration passed its benchmark."));
        var canaryBest = results.Where(row => row.Engine == "Canary" && row.MedianSeconds.HasValue).MinBy(row => row.MedianSeconds)
            ?? throw new InvalidOperationException(Loc.T("No Canary configuration passed its benchmark."));
        foreach (var backend in backends)
        {
            foreach (var candidate in ModelManifest.Entries.Where(item => item.Family == "Correction" && File.Exists(models.PathFor(item))))
            {
                if (!BackendRuntimes.Candidates(paths, hardware, "Correction").Contains(backend)) continue;
                if (!await models.VerifyCachedAsync(candidate, token)) continue;
                if (backend != "cpu" && !GpuMemoryPlanner.Fits(vram, (ulong)candidate.Bytes, 1024UL * 1024 * 1024, 512UL * 1024 * 1024, 1024UL * 1024 * 1024)) continue;
                progress?.Report(Loc.T("Correction · {0} · {1} · warmup + 3 measurements", candidate.Quantization, backend));
                await using var arbiter = new LlamaArbiter(runner, paths.CorrectionFor(backend), models.PathFor(candidate), backend, defaultThreads);
                var row = await BenchmarkSession.MeasureAsync("Correction", candidate.Id, backend, defaultThreads, "persistent", seconds,
                    async (iteration, ct) =>
                    {
                        if (iteration == -1) await arbiter.StartAsync(ct);
                        var clock = Stopwatch.StartNew();
                        await arbiter.ResolveAsync("Schulternhalle", "Schulturnhalle", "Ich habe gesehen, dass ein Verein unsere ", " benutzt.", ct);
                        return new(clock.Elapsed.TotalSeconds, arbiter.LoadSeconds, null, null, arbiter.LastTokensPerSecond);
                    }, token);
                results.Add(row); measurements?.Report(row); await SaveResults(directory, results, token);
            }
        }
        var correctionBest = results.Where(row => row.Engine == "Correction" && row.Model == ModelManifest.Entries.First(item => models.PathFor(item) == paths.CorrectionModel).Id && row.MedianSeconds.HasValue).MinBy(row => row.MedianSeconds);
        if (correctionBest is null && correctionInstalled) throw new InvalidOperationException(Loc.T("No correction configuration passed. The previous tuning profile is preserved."));
        var (correctionBackend, correctionThreads) = correctionBest is null ? ("cpu", defaultThreads) : (correctionBest.Backend, correctionBest.Threads);
        var combinedWeights = (ulong)(new FileInfo(paths.WhisperModel).Length + new FileInfo(paths.CanaryModel).Length);
        var parallelSafe = hardware.RamBytes >= 16UL * 1024 * 1024 * 1024 &&
            (whisperBest.Backend == "cpu" || canaryBest.Backend == "cpu" || GpuMemoryPlanner.Fits(vram, combinedWeights, 3UL * 1024 * 1024 * 1024, 0, 2UL * 1024 * 1024 * 1024));
        foreach (var parallel in parallelSafe ? new[] { false, true } : [false])
        {
            progress?.Report(Loc.T("Dual ASR · {0} · warmup + 3 measurements", parallel ? Loc.T("parallel") : Loc.T("sequential")));
            var row = await BenchmarkSession.MeasureAsync("Dual ASR", "selected models", "mixed", 0, parallel ? "parallel" : "sequential", seconds,
                async (_, ct) =>
                {
                    var clock = Stopwatch.StartNew();
                    if (parallel) await Task.WhenAll(Whisper(whisperBest.Backend, whisperBest.Threads, ct), Canary(canaryBest.Backend, canaryBest.Threads, ct));
                    else { await Whisper(whisperBest.Backend, whisperBest.Threads, ct); await Canary(canaryBest.Backend, canaryBest.Threads, ct); }
                    return new(clock.Elapsed.TotalSeconds);
                }, token);
            results.Add(row); measurements?.Report(row); await SaveResults(directory, results, token);
        }
        var strategy = results.Where(row => row.Engine == "Dual ASR" && row.MedianSeconds.HasValue).MinBy(row => row.MedianSeconds)
            ?? throw new InvalidOperationException(Loc.T("No dual-engine strategy passed. The previous tuning profile is preserved."));
        var profile = new ExecutionProfile(fingerprint, whisperBest.Backend, whisperBest.Threads, canaryBest.Backend, canaryBest.Threads,
            correctionBackend, correctionThreads, strategy.Strategy == "parallel", DateTimeOffset.UtcNow, results);
        await File.WriteAllTextAsync(Path.Combine(directory, "profile.json"), JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }), token);
        var destination = Path.Combine(storage.Root, "Config", "tuning-results.json");
        var temporary = destination + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }), token); File.Move(temporary, destination, true);
        await records.SaveAsync(new("benchmarks", profile.MeasuredUtc.ToString("O"), JsonSerializer.Serialize(profile)), token);
        return profile;
    }
    private static Task SaveResults(string directory, List<BenchmarkRow> results, CancellationToken token) =>
        File.WriteAllTextAsync(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }), token);
}
