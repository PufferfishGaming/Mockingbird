using System.IO;
using System.Text.Json;
using TriAsr.App;
using TriAsr.Application;
using TriAsr.Domain;
using TriAsr.Infrastructure;

namespace TriAsr.Ui.SmokeTests;

public sealed class ChunkPlanStageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
    private readonly Guid _job = Guid.NewGuid();
    private readonly RuntimePaths _paths;
    private readonly StoragePaths _storage;
    private readonly JobWorkspace _workspace;
    private string Directory => _workspace.DirectoryFor(_job);
    private string Wave => Path.Combine(Directory, "normalized.wav");

    public ChunkPlanStageTests()
    {
        _storage = new(Path.Combine(_root, "data")); _storage.EnsureDirectories(); _workspace = new(_storage);
        _paths = new RuntimePaths(Path.Combine(_root, "app"));
        System.IO.Directory.CreateDirectory(Directory);
        WriteSilence(Wave, 40);
    }
    public void Dispose() => TestCleanup.Delete(_root);

    private static void WriteSilence(string path, int seconds)
    {
        var bytes = seconds * 32_000;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8); writer.Write(36 + bytes); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(bytes); writer.Write(new byte[bytes]);
    }
    private void InstallDetector()
    {
        foreach (var file in new[] { _paths.VadTool, _paths.VadModel }) { System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "stand-in"); }
    }
    private LocalTranscriptionStages Stages(IProcessRunner runner) => new(_workspace, new TriAsr.Audio.FfmpegNormalizer(runner, "unused"), runner,
        _paths, _storage, new ModelStore(_root), new Records(), new TriAsr.Hardware.ResourceGovernor(() => 24, () => TriAsr.Hardware.PowerSource.Ac),
        new SettingsStore(_storage, Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsStore>.Instance));
    private bool Has(string name) => File.Exists(Path.Combine(Directory, name));

    [Fact]
    public async Task ThePlanIsSavedWithPaddedSpeechAndNonSpeechBetween()
    {
        InstallDetector();
        var runner = new Runner(new(0, "Detected 2 speech segments:\nSpeech segment 0: start = 1000.00, end = 1400.00\nSpeech segment 1: start = 3000.00, end = 3400.00\n", "", 1));
        await Stages(runner).PlanChunksAsync(_job, Wave, 12, default);
        var plan = JsonSerializer.Deserialize<ChunkPlan>(await File.ReadAllTextAsync(Path.Combine(Directory, "chunks.json")))!;
        Assert.Equal(40_000, plan.DurationMs);
        Assert.Equal("silero-v5.1.2", plan.Source);
        Assert.Equal([(0L, 9_800L, false), (9_800L, 14_200L, true), (14_200L, 29_800L, false), (29_800L, 34_200L, true), (34_200L, 40_000L, false)],
            plan.Chunks.Select(chunk => (chunk.StartMs, chunk.EndMs, chunk.IsSpeech)));
        Assert.Equal("4", runner.Requests[0].Arguments[runner.Requests[0].Arguments.ToList().IndexOf("-t") + 1]); // the detector never gets more than four threads
        Assert.False(Has("chunks.skipped.json") || Has("chunks.failed.json"));
    }

    [Fact]
    public async Task ResumingAJobKeepsTheSavedPlanAndDoesNotRunTheDetectorAgain()
    {
        InstallDetector();
        var runner = new Runner(new(0, "Detected 0 speech segments:\n", "", 1));
        var stages = Stages(runner);
        await stages.PlanChunksAsync(_job, Wave, 4, default);
        await stages.PlanChunksAsync(_job, Wave, 4, default);
        Assert.Single(runner.Requests);
        Assert.True(Has("chunks.json"));
    }

    [Fact]
    public async Task AMissingDetectorLeavesEvidenceAndDoesNotStopTheJob()
    {
        var runner = new Runner(new(0, "", "", 1));
        await Stages(runner).PlanChunksAsync(_job, Wave, 4, default);
        Assert.Empty(runner.Requests);
        Assert.True(Has("chunks.skipped.json"));
        Assert.False(Has("chunks.json"));
    }

    [Fact]
    public async Task AFailingDetectorLeavesEvidenceAndDoesNotStopTheJob()
    {
        InstallDetector();
        await Stages(new Runner(new(1, "", "access violation", 1))).PlanChunksAsync(_job, Wave, 4, default);
        Assert.True(Has("chunks.failed.json"));
        Assert.Contains("access violation", await File.ReadAllTextAsync(Path.Combine(Directory, "chunks.failed.json")));
        Assert.False(Has("chunks.json"));
    }

    [Fact]
    public async Task CancellingTheJobIsNotSwallowed()
    {
        InstallDetector();
        using var source = new CancellationTokenSource(); source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Stages(new Runner(new(0, "", "", 1), cancel: true)).PlanChunksAsync(_job, Wave, 4, source.Token));
        Assert.False(Has("chunks.failed.json"));
    }

    [Fact]
    public void TheSpeechDetectorDoesNotChangeTheRuntimeFingerprint()
    {
        var engine = Path.Combine(_paths.Root, "Runtimes", "Whisper-Vulkan", "whisper-cli.exe");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(engine)!);
        File.WriteAllText(engine, "engine");
        var before = _paths.ConfigurationFingerprint("hardware");
        InstallDetector();
        Assert.Equal(before, _paths.ConfigurationFingerprint("hardware"));
        File.WriteAllText(Path.Combine(_paths.Root, "Runtimes", "Whisper-Vulkan", "another-engine.exe"), "x");
        Assert.NotEqual(before, _paths.ConfigurationFingerprint("hardware"));
    }

    private sealed class Records : IRecordRepository
    {
        public Task SaveAsync(WorkspaceRecord record, CancellationToken token = default) => Task.CompletedTask;
        public Task<IReadOnlyList<WorkspaceRecord>> ListAsync(string kind, Guid? jobId = null, CancellationToken token = default) => Task.FromResult<IReadOnlyList<WorkspaceRecord>>([]);
    }
    private sealed class Runner(ProcessResult result, bool cancel = false) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (cancel) cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }
}
