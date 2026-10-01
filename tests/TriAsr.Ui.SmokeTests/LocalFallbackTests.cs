using System.IO;
using System.Text.Json;
using TriAsr.App;
using TriAsr.Application;
using TriAsr.Domain;
using TriAsr.Engine.Canary;
using TriAsr.Infrastructure;
using TriAsr.Export;

namespace TriAsr.Ui.SmokeTests;

public sealed class LocalFallbackTests
{
    [Fact]
    public async Task MissingSelectedCanaryModelExplainsTheDownloadInsteadOfLaunchingGpuAndCpuRetries()
    {
        using var fixture = new Fixture();
        File.Delete(fixture.ModelPath);
        var runner = new CanaryRunner();
        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => fixture.Stages(runner).ExecuteAsync(fixture.Job, JobState.RunningCanary, default));
        Assert.Contains("not downloaded", error.Message);
        Assert.Contains("Balanced", error.Message);
        Assert.Empty(runner.Backends);
    }
    [Fact]
    public void RepetitionWarningsAppearInTheListeningFilterEvenWhenEnginesAgree()
    {
        var region = new ReviewRegion(new(0, 20000, "original", "original", "original", "agreement",
            Warnings: [TriAsr.Fusion.TranscriptQuality.RepetitionWarning]));
        Assert.True(region.IsUncertain);
        Assert.Contains("Possible recognition loop", region.Evidence);
        Assert.Equal("original", region.Snapshot().FinalText);
    }
    [Fact]
    public async Task FailedVulkanCanaryRetriesCpuAndKeepsBothAttemptLogs()
    {
        using var fixture = new Fixture();
        var runner = new CanaryRunner();
        var stages = fixture.Stages(runner);
        await stages.ExecuteAsync(fixture.Job, JobState.RunningCanary, default);
        var result = JsonSerializer.Deserialize<CanaryNative.Result>(File.ReadAllText(Path.Combine(fixture.Directory, "canary.json")))!;
        Assert.Equal("cpu", result.Transcript.ActualBackend);
        Assert.Equal(new[] { "vulkan", "cpu" }, runner.Backends);
        Assert.Contains("GPU unavailable", File.ReadAllText(Path.Combine(fixture.Directory, "Canary/runtime.stderr.txt")));
        Assert.True(File.Exists(Path.Combine(fixture.Directory, "CanaryCpu/raw.json")));
    }
    [Fact]
    public async Task MissingCanaryProducesUncertainReviewWithoutLlmOrInventedTiming()
    {
        using var fixture = new Fixture();
        var stages = fixture.Stages(new CanaryRunner());
        var transcript = new EngineTranscript("Whisper", "model", "runtime", "cpu", "cpu", "CPU", "de", 1, 1,
            [new(125, 900, "Original speech")], "Original speech", true);
        await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "whisper.json"), JsonSerializer.Serialize(transcript));
        Assert.True(await stages.RecoverSpeechFailureAsync(fixture.Job, JobState.RunningCanary, new IOException("Failed"), default));
        foreach (var stage in new[] { JobState.Aligning, JobState.Correcting, JobState.Finalizing })
            await stages.ExecuteAsync(fixture.Job, stage, default);
        var result = await stages.LoadFinalAsync(fixture.Job.Id);
        var region = Assert.Single(result.Regions);
        Assert.Equal("Original speech", region.FinalText);
        Assert.Equal(125, region.StartMs);
        Assert.Equal(900, region.EndMs);
        Assert.Equal("single-asr-needs-listening", region.Source);
        Assert.True(new ReviewRegion(region).IsUncertain);
    }
    [Fact]
    public async Task MissingWhisperPreservesCanaryTextWithoutInventingSubtitles()
    {
        using var fixture = new Fixture();
        var stages = fixture.Stages(new CanaryRunner());
        var transcript = new EngineTranscript("Canary", "model", "runtime", "cpu", "cpu", "CPU", "de", 1, 1, [], "Original Canary speech", false);
        await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "canary.json"), JsonSerializer.Serialize(new CanaryNative.Result(transcript, [transcript.Text], "cpu")));
        Assert.True(await stages.RecoverSpeechFailureAsync(fixture.Job, JobState.RunningWhisper, new IOException("Failed"), default));
        foreach (var stage in new[] { JobState.Aligning, JobState.Correcting, JobState.Finalizing }) await stages.ExecuteAsync(fixture.Job, stage, default);
        var result = await stages.LoadFinalAsync(fixture.Job.Id);
        var region = Assert.Single(result.Regions);
        Assert.Equal(transcript.Text, region.FinalText);
        Assert.False(region.NativeTimestamps);
        Assert.Equal("No timestamps", new ReviewRegion(region).Time);
        await Assert.ThrowsAsync<InvalidDataException>(() => TranscriptExporter.SaveAsync(result, Path.Combine(fixture.Directory, "invalid.srt")));
        await TranscriptExporter.SaveAsync(result, Path.Combine(fixture.Directory, "valid.csv"));
        Assert.StartsWith(",,", File.ReadAllLines(Path.Combine(fixture.Directory, "valid.csv"))[1]);
    }
    [Fact]
    public async Task BothFailedEnginesDoNotProduceAFalseFinalResult()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Stages(new CanaryRunner()).ExecuteAsync(fixture.Job, JobState.Aligning, default));
        Assert.False(File.Exists(Path.Combine(fixture.Directory, "final.json")));
    }
    [Fact]
    public async Task LanguageOutsideCanaryCoverageUsesWhisperWithoutCallingCanaryOrLlm()
    {
        using var fixture = new Fixture();
        var runner = new CanaryRunner(); var stages = fixture.Stages(runner);
        await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "language.json"), "{\"Language\":\"ja\",\"Confidence\":1}");
        var transcript = new EngineTranscript("Whisper", "model", "runtime", "cpu", "cpu", "CPU", "ja", 1, 1,
            [new(125, 900, "こんにちは")], "こんにちは", true);
        await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "whisper.json"), JsonSerializer.Serialize(transcript));
        foreach (var stage in new[] { JobState.RunningCanary, JobState.Aligning, JobState.Correcting, JobState.Finalizing }) await stages.ExecuteAsync(fixture.Job, stage, default);
        Assert.Empty(runner.Backends);
        var result = await stages.LoadFinalAsync(fixture.Job.Id);
        Assert.Equal("ja", result.Language); var region = Assert.Single(result.Regions);
        Assert.Equal("こんにちは", region.FinalText); Assert.Equal(125, region.StartMs);
        Assert.Equal("single-asr-needs-listening", region.Source);
        Assert.True(File.Exists(Path.Combine(fixture.Directory, "Canary/skipped-language.json")));
    }
    [Fact]
    public void ReadableExportPreservesVerbatimEvidenceAndSpokenWords()
    {
        var original = new FinalTranscript(Guid.NewGuid(), "hu", [new(0, 1000, "Öö,  hát  igen ,\nigen.", "raw", "raw2", "manual")]);
        var copy = TranscriptExporter.ReadableCopy(original);
        Assert.Equal("Öö, hát igen, igen.", copy.Regions[0].FinalText);
        Assert.Equal("Öö,  hát  igen ,\nigen.", original.Regions[0].FinalText);
        Assert.Equal("raw", copy.Regions[0].WhisperText);
        Assert.Equal("readable-derived/manual", copy.Regions[0].Source);
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
        private readonly StoragePaths _storage;
        private readonly JobWorkspace _workspace;
        public TranscriptionJob Job { get; } = new(Guid.NewGuid(), "source.wav", "de", JobState.Queued, DateTimeOffset.UtcNow);
        public string Directory => _workspace.DirectoryFor(Job.Id);
        public string ModelPath => Path.Combine(_root, "fixture-canary.gguf");
        public Fixture()
        {
            _storage = new(_root); _storage.EnsureDirectories(); _workspace = new(_storage);
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(ModelPath, "fixture");
            File.WriteAllText(Path.Combine(Directory, "configuration.json"), JsonSerializer.Serialize(new
            { Fingerprint="test", WhisperBackend="cpu", WhisperThreads=1, CanaryBackend="vulkan", CanaryThreads=1, CorrectionBackend="cpu", CorrectionThreads=1, ParallelSpeech=false }));
            File.WriteAllText(Path.Combine(Directory, "language.json"), "{\"Language\":\"de\",\"Confidence\":1,\"Votes\":[\"de\"]}");
            using var writer = new BinaryWriter(File.Create(Path.Combine(Directory, "normalized.wav")));
            writer.Write("RIFF"u8); writer.Write(32036); writer.Write("WAVEfmt "u8); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(32000); writer.Write(new byte[32000]);
        }
        public LocalTranscriptionStages Stages(IProcessRunner runner) => new(_workspace,
            new TriAsr.Audio.FfmpegNormalizer(runner, "unused"), runner, new RuntimePaths { CanaryModel = ModelPath }, _storage, new ModelStore(_root), new Records());
        public void Dispose() { System.IO.Directory.Delete(_root, true); }
    }
    private sealed class Records : IRecordRepository
    {
        public Task SaveAsync(WorkspaceRecord record, CancellationToken token = default) => Task.CompletedTask;
        public Task<IReadOnlyList<WorkspaceRecord>> ListAsync(string kind, Guid? jobId = null, CancellationToken token = default) => Task.FromResult<IReadOnlyList<WorkspaceRecord>>([]);
    }
    private sealed class CanaryRunner : IProcessRunner
    {
        public List<string> Backends { get; } = [];
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            var input = JsonSerializer.Deserialize<CanaryRequest>(await File.ReadAllTextAsync(request.Arguments[1], cancellationToken))!;
            Backends.Add(input.Backend);
            if (input.Backend == "vulkan") return new(1, "", "GPU unavailable", 0);
            var transcript = new EngineTranscript("Canary", "model", "runtime", "cpu", "cpu", "CPU", "de", 1, 1, [], "Test speech", false);
            await File.WriteAllTextAsync(input.Output, JsonSerializer.Serialize(new CanaryNative.Result(transcript, ["Test speech"], "cpu")), cancellationToken);
            return new(0, "", "CPU succeeded", 1);
        }
    }
}
