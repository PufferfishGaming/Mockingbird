using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TriAsr.App;
using TriAsr.Application;
using TriAsr.Domain;
using TriAsr.Engine.Canary;
using TriAsr.Infrastructure;

namespace TriAsr.Ui.SmokeTests;

/// <summary>The "Skip silence and music" choice: the setting, what a job remembers, and what each engine is given.</summary>
public sealed class SkipNonSpeechTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
    private readonly Guid _job = Guid.NewGuid();
    private readonly RuntimePaths _paths;
    private readonly StoragePaths _storage;
    private readonly JobWorkspace _workspace;
    private readonly List<(string Title, string Message)> _issues = [];
    private string Directory => _workspace.DirectoryFor(_job);
    private string CanaryModel => Path.Combine(_root, "canary.gguf");

    public SkipNonSpeechTests()
    {
        _storage = new(Path.Combine(_root, "data")); _storage.EnsureDirectories(); _workspace = new(_storage);
        _paths = new RuntimePaths(Path.Combine(_root, "app")) { CanaryModel = CanaryModel, CorrectionModel = Path.Combine(_root, "absent", "correction.gguf") };
        foreach (var file in new[] { _paths.WhisperFor("cpu"), _paths.VadModel, CanaryModel }) { System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "stand-in"); }
        System.IO.Directory.CreateDirectory(Directory);
        WriteSilence(Path.Combine(Directory, "normalized.wav"), 40);
        File.WriteAllText(Path.Combine(Directory, "language.json"), "{\"Language\":\"de\",\"Confidence\":1,\"WindowLanguages\":[\"de\"]}");
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
    private void Configure(bool skip, string job = "auto", bool correction = false)
    {
        File.WriteAllText(Path.Combine(Directory, "configuration.json"), JsonSerializer.Serialize(new JobConfiguration("test", "cpu", 1, "cpu", 1, "cpu", 1, false, skip, correction)));
        _jobRecord = new(_job, "source.wav", job, JobState.Queued, DateTimeOffset.UtcNow);
    }
    private TranscriptionJob _jobRecord = null!;
    private void SavePlan() => File.WriteAllText(Path.Combine(Directory, "chunks.json"), JsonSerializer.Serialize(new ChunkPlan(ChunkPlan.CurrentVersion, 40_000, "test",
        [new(0, 0, 9_800, false, false), new(1, 9_800, 14_200, true, false), new(2, 14_200, 29_800, false, false), new(3, 29_800, 34_200, true, false), new(4, 34_200, 40_000, false, false)])));
    private LocalTranscriptionStages Stages(IProcessRunner runner, SettingsStore? settings = null)
    {
        var stages = new LocalTranscriptionStages(_workspace, new TriAsr.Audio.FfmpegNormalizer(runner, "unused"), runner, _paths, _storage, new ModelStore(_root),
            new Records(), new TriAsr.Hardware.ResourceGovernor(() => 24, () => TriAsr.Hardware.PowerSource.Ac),
            settings ?? new SettingsStore(_storage, Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsStore>.Instance));
        stages.IssueOccurred += (_, issue) => _issues.Add(issue);
        return stages;
    }

    // ---- the setting and the job's own copy of it ------------------------------------------------------------------------------

    private static JobConfiguration Current() => new("fingerprint", "vulkan", 8, "vulkan", 8, "cpu", 4, true);

    [Fact]
    public void ANewJobTakesTheChoiceFromSettings()
    {
        Assert.False(new AppSettings().SkipNonSpeech);
        Assert.True(JobConfiguration.Bind(Current(), null, skipNonSpeechSetting: true).SkipNonSpeech);
        Assert.False(JobConfiguration.Bind(Current(), null, skipNonSpeechSetting: false).SkipNonSpeech);
    }

    [Fact]
    public void AResumedJobKeepsItsChoiceWhateverSettingsSayNow()
    {
        var saved = Current() with { SkipNonSpeech = true };
        Assert.True(JobConfiguration.Bind(Current(), saved, skipNonSpeechSetting: false).SkipNonSpeech);
        Assert.False(JobConfiguration.Bind(Current(), Current(), skipNonSpeechSetting: true).SkipNonSpeech);
    }

    [Fact]
    public void AnythingElseChangingStillStopsAResumedJob()
    {
        var error = Assert.Throws<InvalidDataException>(() => JobConfiguration.Bind(Current() with { WhisperThreads = 12 }, Current(), false));
        Assert.Contains("configuration changed", error.Message);
        Assert.Throws<InvalidDataException>(() => JobConfiguration.Bind(Current() with { Fingerprint = "other" }, Current() with { SkipNonSpeech = true }, true));
    }

    [Fact]
    public void JobsSavedBeforeTheSettingExistedMeanNoSkipping()
    {
        var old = """{"Fingerprint":"f","WhisperBackend":"cpu","WhisperThreads":1,"CanaryBackend":"cpu","CanaryThreads":1,"CorrectionBackend":"cpu","CorrectionThreads":1,"ParallelSpeech":false}""";
        Assert.False(JsonSerializer.Deserialize<JobConfiguration>(old)!.SkipNonSpeech);
    }

    // ---- Whisper ----------------------------------------------------------------------------------------------------------------

    private sealed class Runner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public CanaryRequest? Canary { get; private set; }
        public string? WhisperOutput { get; init; }
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var arguments = request.Arguments.ToList();
            if (arguments.Contains("--canary"))
            {
                var input = JsonSerializer.Deserialize<CanaryRequest>(await File.ReadAllTextAsync(arguments[1], cancellationToken))!;
                Canary = input;
                var transcript = new EngineTranscript("Canary", "model", "runtime", input.Backend, input.Backend, "CPU", "de", 40, 1, [], "Test speech", false);
                await File.WriteAllTextAsync(input.Output, JsonSerializer.Serialize(new CanaryNative.Result(transcript, ["Test speech"], input.Backend)), cancellationToken);
                return new(0, "", "", 1);
            }
            if (arguments.Contains("-ojf"))
                await File.WriteAllTextAsync(arguments[arguments.IndexOf("-of") + 1] + ".json",
                    WhisperOutput ?? """{"result":{"language":"de"},"transcription":[{"offsets":{"from":10000,"to":14000},"text":" Hallo Welt"}]}""", cancellationToken);
            return new(0, "", arguments.Contains("-dl") ? "auto-detected language: de (p = 0.970000)" : "", 1);
        }
    }

    [Fact]
    public async Task WhisperSkipsWhenTheJobSaysSoAndThePlanExists()
    {
        Configure(skip: true); SavePlan();
        var runner = new Runner();
        await Stages(runner).ExecuteAsync(_jobRecord, JobState.RunningWhisper, default);
        var arguments = runner.Requests.Single().Arguments;
        Assert.Contains("--vad", arguments);
        Assert.Equal(_paths.VadModel, arguments[arguments.ToList().IndexOf("-vm") + 1]);
        Assert.Empty(_issues);
    }

    [Fact]
    public async Task WhisperReadsTheWholeRecordingByDefault()
    {
        Configure(skip: false); SavePlan();
        var runner = new Runner();
        await Stages(runner).ExecuteAsync(_jobRecord, JobState.RunningWhisper, default);
        Assert.DoesNotContain("--vad", runner.Requests.Single().Arguments);
        Assert.Empty(_issues);
    }

    [Fact]
    public async Task WithoutAPlanASkippingJobStillTranscribesEverythingAndSaysSo()
    {
        Configure(skip: true); // no chunks.json: the detector was not available when the job started
        var runner = new Runner();
        await Stages(runner).ExecuteAsync(_jobRecord, JobState.RunningWhisper, default);
        Assert.DoesNotContain("--vad", runner.Requests.Single().Arguments);
        Assert.True(File.Exists(Path.Combine(Directory, "Whisper", "skip-unavailable.json")));
        Assert.Contains(_issues, issue => issue.Title.Contains("could not be skipped"));
    }

    // ---- the loop guard -------------------------------------------------------------------------------------------------------

    private static string WhisperJson(IEnumerable<(int From, int To, string Text)> segments) =>
        "{\"result\":{\"language\":\"de\"},\"transcription\":[" + string.Join(",", segments.Select(s => $"{{\"offsets\":{{\"from\":{s.From},\"to\":{s.To}}},\"text\":\" {s.Text}\"}}")) + "]}";

    private static IEnumerable<(int, int, string)> LoopedOutput() =>
        new[] { (0, 4_000, "Hallo zusammen.") }
            .Concat(Enumerable.Range(0, 40).Select(i => (10_000 + i * 2_000, 12_000 + i * 2_000, "Egy kicsit mi tortenik.")))
            .Concat([(100_000, 104_000, "Und weiter geht es.")]);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ALoopingWhisperIsCleanedKeepsItsRawOutputAndTellsTheUser(bool skipping, bool suggestsSkipping)
    {
        Configure(skip: skipping); if (skipping) SavePlan();
        var runner = new Runner { WhisperOutput = WhisperJson(LoopedOutput()) };
        await Stages(runner).ExecuteAsync(_jobRecord, JobState.RunningWhisper, default);

        var saved = JsonSerializer.Deserialize<EngineTranscript>(await File.ReadAllTextAsync(Path.Combine(Directory, "whisper.json")))!;
        Assert.Equal(["Hallo zusammen.", "Egy kicsit mi tortenik.", "Und weiter geht es."], saved.Segments.Select(segment => segment.Text));
        Assert.Equal("Hallo zusammen. Egy kicsit mi tortenik. Und weiter geht es.", saved.Text);
        // The recogniser output is evidence and stays whole.
        using var raw = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Directory, "Whisper", "raw.json")));
        Assert.Equal(42, raw.RootElement.GetProperty("transcription").GetArrayLength());
        // What was removed is written down.
        using var loops = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Directory, "Whisper", "loops.json")));
        Assert.Equal(39, loops.RootElement.GetProperty("Removed").GetInt32());
        var issue = Assert.Single(_issues, item => item.Title == "Repeated text removed");
        Assert.Contains("39 segments were removed", issue.Message);
        Assert.Equal(suggestsSkipping, issue.Message.Contains("Skip silence and music"));
    }

    [Fact]
    public async Task AnOrdinaryWhisperOutputIsSavedUntouchedWithoutAnyNotice()
    {
        Configure(skip: false);
        var runner = new Runner { WhisperOutput = WhisperJson(Enumerable.Range(0, 30).Select(i => (i * 3_000, i * 3_000 + 2_500, $"Das ist der Satz mit der Nummer {i}."))) };
        await Stages(runner).ExecuteAsync(_jobRecord, JobState.RunningWhisper, default);
        var saved = JsonSerializer.Deserialize<EngineTranscript>(await File.ReadAllTextAsync(Path.Combine(Directory, "whisper.json")))!;
        Assert.Equal(30, saved.Segments.Count);
        Assert.False(File.Exists(Path.Combine(Directory, "Whisper", "loops.json")));
        Assert.Empty(_issues);
    }

    [Fact]
    public async Task ACollapsedSongEndingIsCleanedAndListedWithoutTouchingTheRawOutput()
    {
        Configure(skip: false);
        // Eight normal lines, then seven lines written four times each at six words per second (1 s segments), as measured on a real song.
        var output = Enumerable.Range(0, 8).Select(i => (i * 15_000, i * 15_000 + 12_000, $"Das ist die normale Zeile Nummer {i} hier."))
            .Concat(Enumerable.Range(0, 28).Select(i => (130_000 + i * 3_000, 131_000 + i * 3_000, $"Erfundene Zeile {i % 7} vom Ende des Liedes")));
        var runner = new Runner { WhisperOutput = WhisperJson(output) };
        await Stages(runner).ExecuteAsync(_jobRecord, JobState.RunningWhisper, default);

        var saved = JsonSerializer.Deserialize<EngineTranscript>(await File.ReadAllTextAsync(Path.Combine(Directory, "whisper.json")))!;
        Assert.Equal(8 + 7, saved.Segments.Count);
        using var raw = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Directory, "Whisper", "raw.json")));
        Assert.Equal(36, raw.RootElement.GetProperty("transcription").GetArrayLength());
        using var loops = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Directory, "Whisper", "loops.json")));
        Assert.Equal(21, loops.RootElement.GetProperty("Removed").GetInt32());
        Assert.Equal(1, loops.RootElement.GetProperty("FastRuns").GetArrayLength());
        Assert.Equal(0, loops.RootElement.GetProperty("Runs").GetArrayLength());
        Assert.Contains("21 segments were removed", Assert.Single(_issues, item => item.Title == "Repeated text removed").Message);
    }

    // ---- text only Whisper wrote, where no speech was detected ----------------------------------------------------------------------

    [Fact]
    public async Task InventedTextWithoutSupportIsLeftOutOfTheComparisonButKeptAsEvidence()
    {
        Configure(skip: false); SavePlan();   // speech chunks at 9.8-14.2 s and 29.8-34.2 s; the rest of the 40 s is quiet
        var whisper = new EngineTranscript("Whisper", "model", "runtime", "cpu", "cpu", "CPU", "de", 40, 1,
            [new(10_000, 14_000, "Hallo zusammen das ist ein Test"), new(36_000, 39_000, "Vertraue und glaube es hilft es heilt")],
            "Hallo zusammen das ist ein Test Vertraue und glaube es hilft es heilt", true);
        var canary = new EngineTranscript("Canary", "model", "runtime", "cpu", "cpu", "CPU", "de", 40, 1, [], "Hallo zusammen das ist ein Test", false);
        await File.WriteAllTextAsync(Path.Combine(Directory, "whisper.json"), JsonSerializer.Serialize(whisper));
        await File.WriteAllTextAsync(Path.Combine(Directory, "canary.json"), JsonSerializer.Serialize(new CanaryNative.Result(canary, ["x"], "cpu")));

        await Stages(new Runner()).ExecuteAsync(_jobRecord, JobState.Aligning, default);

        var comparison = await File.ReadAllTextAsync(Path.Combine(Directory, "comparison.json"));
        Assert.Contains("Hallo zusammen", comparison);
        Assert.DoesNotContain("Vertraue", comparison);
        // whisper.json is untouched, and the removed segment is written down.
        Assert.Contains("Vertraue", await File.ReadAllTextAsync(Path.Combine(Directory, "whisper.json")));
        using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Directory, "Whisper", "unsupported.json")));
        Assert.Equal(1, evidence.RootElement.GetProperty("Removed").GetArrayLength());
        Assert.Single(_issues, item => item.Title == "Unsupported text left out");
    }

    [Fact]
    public async Task WithoutAChunkPlanNothingIsLeftOut()
    {
        Configure(skip: false);
        var whisper = new EngineTranscript("Whisper", "model", "runtime", "cpu", "cpu", "CPU", "de", 40, 1,
            [new(36_000, 39_000, "Vertraue und glaube es hilft es heilt")], "Vertraue und glaube es hilft es heilt", true);
        var canary = new EngineTranscript("Canary", "model", "runtime", "cpu", "cpu", "CPU", "de", 40, 1, [], "Hallo zusammen", false);
        await File.WriteAllTextAsync(Path.Combine(Directory, "whisper.json"), JsonSerializer.Serialize(whisper));
        await File.WriteAllTextAsync(Path.Combine(Directory, "canary.json"), JsonSerializer.Serialize(new CanaryNative.Result(canary, ["x"], "cpu")));
        await Stages(new Runner()).ExecuteAsync(_jobRecord, JobState.Aligning, default);
        Assert.Contains("Vertraue", await File.ReadAllTextAsync(Path.Combine(Directory, "comparison.json")));
        Assert.False(File.Exists(Path.Combine(Directory, "Whisper", "unsupported.json")));
        Assert.Empty(_issues);
    }

    // ---- the correction model ---------------------------------------------------------------------------------------------------

    [Fact]
    public void TheCorrectionModelIsOnByDefaultAndAJobKeepsItsOwnChoice()
    {
        Assert.True(new AppSettings().UseCorrectionModel);
        Assert.True(JobConfiguration.Bind(Current(), null, false, useCorrectionModelSetting: true).UseCorrectionModel);
        Assert.False(JobConfiguration.Bind(Current(), null, true).UseCorrectionModel);
        // A resumed job keeps what it started with, whatever Settings say now.
        Assert.True(JobConfiguration.Bind(Current(), Current() with { UseCorrectionModel = true }, false, false).UseCorrectionModel);
        Assert.False(JobConfiguration.Bind(Current(), Current(), false, true).UseCorrectionModel);
        // Jobs saved before the setting existed did not use it.
        var old = """{"Fingerprint":"f","WhisperBackend":"cpu","WhisperThreads":1,"CanaryBackend":"cpu","CanaryThreads":1,"CorrectionBackend":"cpu","CorrectionThreads":1,"ParallelSpeech":false,"SkipNonSpeech":true}""";
        Assert.False(JsonSerializer.Deserialize<JobConfiguration>(old)!.UseCorrectionModel);
    }

    [Fact]
    public async Task WithoutTheCorrectionModelNothingIsLaunchedAndWhisperKeepsItsWords()
    {
        Configure(skip: false);
        var whisper = new EngineTranscript("Whisper", "model", "runtime", "cpu", "cpu", "CPU", "de", 40, 1,
            [new(1_000, 6_000, "Wir treffen uns am Montag in der Schulternhalle zum Training.")], "Wir treffen uns am Montag in der Schulternhalle zum Training.", true);
        var canary = new EngineTranscript("Canary", "model", "runtime", "cpu", "cpu", "CPU", "de", 40, 1, [], "Wir treffen uns am Montag in der Schulturnhalle zum Training.", false);
        var comparison = TriAsr.Fusion.DisagreementDetector.Compare(whisper, canary);
        Assert.NotEmpty(comparison.Disagreements);   // the engines really do disagree about one word
        await File.WriteAllTextAsync(Path.Combine(Directory, "comparison.json"), JsonSerializer.Serialize(comparison));
        var runner = new Runner();
        var stages = Stages(runner);

        await stages.ExecuteAsync(_jobRecord, JobState.Correcting, default);
        await stages.ExecuteAsync(_jobRecord, JobState.Finalizing, default);

        Assert.Empty(runner.Requests);                // no model server, no download check
        var final = await stages.LoadFinalAsync(_job);
        var region = Assert.Single(final.Regions);
        Assert.Contains("Schulternhalle", region.FinalText);       // Whisper's word stays, the engines' disagreement is marked
        Assert.Equal("uncertain", region.Source);
        Assert.Empty(_issues);
    }

    [Fact]
    public async Task TheSettingOnWithoutTheModelDownloadedBehavesAsIfItWereOffAndStaysQuiet()
    {
        // The setting is on for everybody now, so a fresh install has it on before the model is downloaded: that must not raise an alert per job.
        Configure(skip: false, correction: true);
        var whisper = new EngineTranscript("Whisper", "model", "runtime", "cpu", "cpu", "CPU", "de", 40, 1,
            [new(1_000, 6_000, "Wir treffen uns am Montag in der Schulternhalle zum Training.")], "Wir treffen uns am Montag in der Schulternhalle zum Training.", true);
        var canary = new EngineTranscript("Canary", "model", "runtime", "cpu", "cpu", "CPU", "de", 40, 1, [], "Wir treffen uns am Montag in der Schulturnhalle zum Training.", false);
        await File.WriteAllTextAsync(Path.Combine(Directory, "comparison.json"), JsonSerializer.Serialize(TriAsr.Fusion.DisagreementDetector.Compare(whisper, canary)));
        var runner = new Runner();
        var stages = Stages(runner);
        Assert.False(File.Exists(_paths.CorrectionModel));

        await stages.ExecuteAsync(_jobRecord, JobState.Correcting, default);
        await stages.ExecuteAsync(_jobRecord, JobState.Finalizing, default);

        Assert.Empty(runner.Requests);
        Assert.Empty(_issues);
        var region = Assert.Single((await stages.LoadFinalAsync(_job)).Regions);
        Assert.Contains("Schulternhalle", region.FinalText);
        Assert.Equal("uncertain", region.Source);
    }

    // ---- Canary ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CanaryGetsOnlyTheSpeechWindowsWhenSkipping()
    {
        Configure(skip: true); SavePlan();
        var runner = new Runner();
        await Stages(runner).ExecuteAsync(_jobRecord, JobState.RunningCanary, default);
        var request = runner.Canary!;
        Assert.Equal([1, 3], request.Windows!.Select(window => window.Index));
        Assert.Equal(Path.Combine(Directory, "Canary", "windows"), Path.GetFullPath(request.CheckpointDirectory!));
        Assert.Empty(_issues);
    }

    [Fact]
    public async Task WithoutSkippingCanaryReadsTheWholeFileAsBeforeEvenWhenAPlanExists()
    {
        // The default must not change anybody's results: measured on a song, Canary fed from isolated non-speech pieces lost a fifth of its words.
        Configure(skip: false); SavePlan();
        var runner = new Runner();
        await Stages(runner).ExecuteAsync(_jobRecord, JobState.RunningCanary, default);
        Assert.Null(runner.Canary!.Windows);
        Assert.Null(runner.Canary.CheckpointDirectory);
        Assert.Empty(_issues);
    }

    [Fact]
    public async Task CanaryReadsTheWholeFileAsBeforeWhenThereIsNoPlan()
    {
        Configure(skip: false);
        var runner = new Runner();
        await Stages(runner).ExecuteAsync(_jobRecord, JobState.RunningCanary, default);
        Assert.Null(runner.Canary!.Windows);
        Assert.Null(runner.Canary.CheckpointDirectory);
        Assert.Empty(_issues);
    }

    [Fact]
    public async Task CanarySaysSoWhenAskedToSkipWithoutAPlan()
    {
        Configure(skip: true);
        var runner = new Runner();
        await Stages(runner).ExecuteAsync(_jobRecord, JobState.RunningCanary, default);
        Assert.Null(runner.Canary!.Windows);
        Assert.True(File.Exists(Path.Combine(Directory, "Canary", "skip-unavailable.json")));
        Assert.Contains(_issues, issue => issue.Title.Contains("could not be skipped"));
    }

    [Fact]
    public async Task APlanForAnotherLengthOfAudioIsIgnored()
    {
        Configure(skip: true);
        File.WriteAllText(Path.Combine(Directory, "chunks.json"), JsonSerializer.Serialize(new ChunkPlan(ChunkPlan.CurrentVersion, 90_000, "test", [new(0, 0, 90_000, true, false)])));
        var runner = new Runner();
        await Stages(runner).ExecuteAsync(_jobRecord, JobState.RunningCanary, default);
        Assert.Null(runner.Canary!.Windows);
        Assert.Contains(_issues, issue => issue.Title.Contains("could not be skipped"));
    }

    // ---- language samples ---------------------------------------------------------------------------------------------------------

    private static IEnumerable<string> SampleStarts(Runner runner) => runner.Requests.Where(request => request.Arguments.Contains("-ss"))
        .Select(request => request.Arguments[request.Arguments.ToList().IndexOf("-ss") + 1]);

    [Fact]
    public async Task LanguageIsSampledWhereTheSpeechIsWhenSkipping()
    {
        Configure(skip: true, job: "auto"); SavePlan();
        File.Delete(Path.Combine(Directory, "language.json")); // detect it for real this time
        var runner = new Runner();
        await Stages(runner).ExecuteAsync(_jobRecord, JobState.DetectingLanguage, default);
        // Speech starts at 9.8 s and 29.8 s; the second sample is moved back so that its 15 s still fit into the 40 s recording.
        Assert.Equal(["9.8", "25"], SampleStarts(runner));
    }

    [Fact]
    public async Task LanguageSamplesKeepTheirOldPlacesWhenNotSkippingEvenWithAPlan()
    {
        Configure(skip: false, job: "auto"); SavePlan();
        File.Delete(Path.Combine(Directory, "language.json"));
        var runner = new Runner();
        await Stages(runner).ExecuteAsync(_jobRecord, JobState.DetectingLanguage, default);
        Assert.Equal(["0", "12.5", "25"], SampleStarts(runner));
    }

    [Fact]
    public async Task LanguageSamplesKeepTheirOldPlacesWithoutAPlan()
    {
        Configure(skip: false, job: "auto");
        File.Delete(Path.Combine(Directory, "language.json"));
        var runner = new Runner();
        await Stages(runner).ExecuteAsync(_jobRecord, JobState.DetectingLanguage, default);
        Assert.Equal(["0", "12.5", "25"], SampleStarts(runner));
    }

    private sealed class Records : IRecordRepository
    {
        public Task SaveAsync(WorkspaceRecord record, CancellationToken token = default) => Task.CompletedTask;
        public Task<IReadOnlyList<WorkspaceRecord>> ListAsync(string kind, Guid? jobId = null, CancellationToken token = default) => Task.FromResult<IReadOnlyList<WorkspaceRecord>>([]);
    }
}

public sealed class SkipNonSpeechSettingTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline) { if (await condition()) return; await Task.Delay(50); }
        throw new TimeoutException("The expected state was not reached.");
    }

    /// <summary>A save ends by writing to the local database; the host must not be disposed before that has finished.</summary>
    private static async Task WaitForSavedAsync(App.ShellViewModel shell)
    {
        await WaitForAsync(() => Task.FromResult(shell.Status == "Preferences saved locally"));
        await Task.Delay(250);
    }

    [Fact]
    public async Task ItIsOffByDefaultIsSavedAndSurvivesARestart()
    {
        var root = NewRoot();
        try
        {
            using (var host = App.App.CreateHost(root))
            {
                var shell = host.Services.GetRequiredService<App.ShellViewModel>();
                await shell.InitializeAsync();
                Assert.False(shell.SkipNonSpeech);
                shell.SkipNonSpeech = true;
                var store = host.Services.GetRequiredService<App.SettingsStore>();
                await WaitForAsync(async () => (await store.LoadAsync()).SkipNonSpeech);
                await WaitForSavedAsync(shell);
            }
            using (var host = App.App.CreateHost(root))
            {
                var shell = host.Services.GetRequiredService<App.ShellViewModel>();
                await shell.InitializeAsync();
                Assert.True(shell.SkipNonSpeech);
            }
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task TheCorrectionModelChoiceIsOnByDefaultSavedAndKept()
    {
        var root = NewRoot();
        try
        {
            using (var host = App.App.CreateHost(root))
            {
                var shell = host.Services.GetRequiredService<App.ShellViewModel>();
                await shell.InitializeAsync();
                Assert.True(shell.UseCorrectionModel);
                shell.UseCorrectionModel = false;
                var store = host.Services.GetRequiredService<App.SettingsStore>();
                await WaitForAsync(async () => !(await store.LoadAsync()).UseCorrectionModel);
                await WaitForSavedAsync(shell);
            }
            using (var host = App.App.CreateHost(root))
            {
                var shell = host.Services.GetRequiredService<App.ShellViewModel>();
                await shell.InitializeAsync();
                Assert.False(shell.UseCorrectionModel);   // switching it off is remembered
                Assert.False(shell.SkipNonSpeech);   // the two choices are independent
            }
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task SettingsSavedByEarlierVersionsMeanOff()
    {
        var root = NewRoot();
        try
        {
            using var host = App.App.CreateHost(root);
            var store = host.Services.GetRequiredService<App.SettingsStore>();
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            await File.WriteAllTextAsync(store.FilePath, "{\"theme\":\"System\",\"density\":\"Comfortable\",\"version\":1,\"resourceProfile\":\"Quiet\"}");
            var loaded = await store.LoadAsync();
            Assert.False(loaded.SkipNonSpeech);
            Assert.Equal("Quiet", loaded.ResourceProfile);
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task OtherPreferenceChangesKeepIt()
    {
        var root = NewRoot();
        try
        {
            using var host = App.App.CreateHost(root);
            var shell = host.Services.GetRequiredService<App.ShellViewModel>();
            var store = host.Services.GetRequiredService<App.SettingsStore>();
            await shell.InitializeAsync();
            shell.SkipNonSpeech = true;
            shell.SelectedDensity = "Compact";
            await WaitForAsync(async () => { var s = await store.LoadAsync(); return s.Density == "Compact" && s.SkipNonSpeech; });
            await WaitForSavedAsync(shell);
        }
        finally { TestCleanup.Delete(root); }
    }
}
