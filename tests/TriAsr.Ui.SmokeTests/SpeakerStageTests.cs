using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using TriAsr.App;
using TriAsr.Application;
using TriAsr.Domain;
using TriAsr.Engine.Canary;
using TriAsr.Export;
using TriAsr.Fusion;
using TriAsr.Infrastructure;

namespace TriAsr.Ui.SmokeTests;

/// <summary>
/// Telling the speakers of a recording apart, through the stages (with a stand-in for the speaker program), in the exports and in the API.
/// </summary>
public sealed class SpeakerStageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
    private readonly Guid _job = Guid.NewGuid();
    private readonly RuntimePaths _paths;
    private readonly StoragePaths _storage;
    private readonly JobWorkspace _workspace;
    private readonly List<(string Title, string Message)> _issues = [];
    private string Directory => _workspace.DirectoryFor(_job);

    public SpeakerStageTests()
    {
        _storage = new(Path.Combine(_root, "data")); _storage.EnsureDirectories(); _workspace = new(_storage);
        _paths = new RuntimePaths(Path.Combine(_root, "app")) { CanaryModel = Path.Combine(_root, "canary.gguf"), CorrectionModel = Path.Combine(_root, "absent", "correction.gguf") };
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(Path.Combine(Directory, "configuration.json"), JsonSerializer.Serialize(new JobConfiguration("test", "cpu", 1, "cpu", 1, "cpu", 1, false, false, false)));
        using (var writer = new BinaryWriter(File.Create(Path.Combine(Directory, "normalized.wav"))))
        {
            var bytes = 10 * 32_000;
            writer.Write("RIFF"u8); writer.Write(36 + bytes); writer.Write("WAVEfmt "u8); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(bytes); writer.Write(new byte[bytes]);
        }
        // Whisper wrote one segment over a question and its answer, and a second one; its full output has the words with their times.
        var whisper = new EngineTranscript("Whisper", "model", "runtime", "cpu", "cpu", "CPU", "en", 10, 1,
            [new(1_000, 5_000, "Are you coming? Yes, at nine."), new(6_000, 8_000, "Good.")], "Are you coming? Yes, at nine. Good.", true);
        File.WriteAllText(Path.Combine(Directory, "whisper.json"), JsonSerializer.Serialize(whisper));
        System.IO.Directory.CreateDirectory(Path.Combine(Directory, "Whisper"));
        File.WriteAllText(Path.Combine(Directory, "Whisper", "raw.json"), """
            {"result":{"language":"en"},"transcription":[
             {"offsets":{"from":1000,"to":5000},"text":" Are you coming? Yes, at nine.","tokens":[
              {"text":" Are","offsets":{"from":1000,"to":1300}},{"text":" you","offsets":{"from":1300,"to":1500}},{"text":" coming","offsets":{"from":1500,"to":1900}},{"text":"?","offsets":{"from":1900,"to":2000}},
              {"text":" Yes","offsets":{"from":3000,"to":3300}},{"text":",","offsets":{"from":3300,"to":3400}},{"text":" at","offsets":{"from":3400,"to":3600}},{"text":" nine.","offsets":{"from":3600,"to":4000}}]},
             {"offsets":{"from":6000,"to":8000},"text":" Good.","tokens":[{"text":" Good.","offsets":{"from":6200,"to":6800}}]}]}
            """);
        var canary = new EngineTranscript("Canary", "model", "runtime", "cpu", "cpu", "CPU", "en", 10, 1, [], "Are you coming? Yes, at nine. Good.", false);
        File.WriteAllText(Path.Combine(Directory, "canary.json"), JsonSerializer.Serialize(new CanaryNative.Result(canary, ["x"], "cpu")));
    }

    public void Dispose() => TestCleanup.Delete(_root);

    private void InstallSpeakerProgram()
    {
        foreach (var file in new[] { _paths.SpeakersTool, _paths.SpeakerSegmentationModel, _paths.SpeakerEmbeddingModel })
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "stand-in");
        }
    }

    private void Ask(string speakers) => File.WriteAllText(Path.Combine(Directory, JobWorkspace.OptionsFile), JsonSerializer.Serialize(new JobOptions(speakers)));

    /// <summary>Stands in for the speaker program: the first person asks until 2.2 s, a second one answers from 2.8 s, the first speaks again from 6 s.</summary>
    private sealed class Runner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            request.ErrorLine?.Invoke("progress 50.00%");
            return Task.FromResult(new ProcessResult(0, "Started\n0.800 -- 2.200 speaker_04\n2.800 -- 4.500 speaker_00\n6.000 -- 8.000 speaker_04\nElapsed seconds: 1.0 s\n", "", 1));
        }
    }

    private LocalTranscriptionStages Stages(IProcessRunner runner)
    {
        var stages = new LocalTranscriptionStages(_workspace, new TriAsr.Audio.FfmpegNormalizer(runner, "unused"), runner, _paths, _storage, new ModelStore(_root),
            new Records(), new TriAsr.Hardware.ResourceGovernor(() => 24, () => TriAsr.Hardware.PowerSource.Ac),
            new SettingsStore(_storage, Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsStore>.Instance));
        stages.IssueOccurred += (_, issue) => _issues.Add(issue);
        return stages;
    }

    private sealed class Records : IRecordRepository
    {
        public Task SaveAsync(WorkspaceRecord record, CancellationToken token = default) => Task.CompletedTask;
        public Task<IReadOnlyList<WorkspaceRecord>> ListAsync(string kind, Guid? jobId = null, CancellationToken token = default) => Task.FromResult<IReadOnlyList<WorkspaceRecord>>([]);
    }

    private TranscriptionJob Job => new(_job, "source.wav", "en", JobState.Queued, DateTimeOffset.UtcNow);

    private IReadOnlyList<FinalRegion> Regions() => JsonSerializer.Deserialize<ComparisonResult>(File.ReadAllText(Path.Combine(Directory, "comparison.json")))!.Regions;

    [Fact]
    public async Task TheSpeakersAreToldApartAndASegmentIsCutWhereTheSpeakerChanges()
    {
        InstallSpeakerProgram(); Ask("auto");
        var runner = new Runner();
        await Stages(runner).ExecuteAsync(Job, JobState.Aligning, default);
        var request = Assert.Single(runner.Requests);
        Assert.Equal(_paths.SpeakersTool, request.Executable);
        Assert.Contains("--clustering.cluster-threshold=1.05", request.Arguments);
        Assert.Equal([("Are you coming?", "1"), ("Yes, at nine.", "2"), ("Good.", "1")], Regions().Select(region => (region.FinalText, region.Speaker!)));
        Assert.Equal([new SpeakerTurn(800, 2_200, "1"), new SpeakerTurn(2_800, 4_500, "2"), new SpeakerTurn(6_000, 8_000, "1")],
            JsonSerializer.Deserialize<SpeakerTurn[]>(File.ReadAllText(Path.Combine(Directory, "speakers.json")))!);
        Assert.True(File.Exists(Path.Combine(Directory, "Speakers", "found.json")));                // what the program found, before tidying
        Assert.Empty(_issues);

        // A resumed job reads the turns again instead of running the program a second time.
        File.Delete(Path.Combine(Directory, "comparison.json"));
        await Stages(runner).ExecuteAsync(Job, JobState.Aligning, default);
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task AJobThatDoesNotAskIsComparedAsBeforeWithoutSpeakers()
    {
        InstallSpeakerProgram();
        var runner = new Runner();
        await Stages(runner).ExecuteAsync(Job, JobState.Aligning, default);
        Assert.Empty(runner.Requests);
        Assert.Equal(["Are you coming? Yes, at nine.", "Good."], Regions().Select(region => region.FinalText));
        Assert.All(Regions(), region => Assert.Null(region.Speaker));
        Assert.DoesNotContain("Speaker", File.ReadAllText(Path.Combine(Directory, "comparison.json")));   // nothing new in the files of a job without speakers
    }

    [Fact]
    public async Task WithoutTheSpeakerProgramTheTranscriptIsMadeWithoutSpeakersAndThePersonIsTold()
    {
        Ask("2");
        var runner = new Runner();
        await Stages(runner).ExecuteAsync(Job, JobState.Aligning, default);
        Assert.Empty(runner.Requests);
        Assert.All(Regions(), region => Assert.Null(region.Speaker));
        Assert.Contains(_issues, issue => issue.Title == "Speakers could not be told apart");
        Assert.True(File.Exists(Path.Combine(Directory, "Speakers", "unavailable.json")));
    }

    [Fact]
    public async Task AFailingSpeakerProgramLeavesTheTranscriptWithoutSpeakers()
    {
        InstallSpeakerProgram(); Ask("auto");
        await Stages(new FailingRunner()).ExecuteAsync(Job, JobState.Aligning, default);
        Assert.All(Regions(), region => Assert.Null(region.Speaker));
        Assert.Contains(_issues, issue => issue.Title == "Speakers could not be told apart");
        Assert.True(File.Exists(Path.Combine(Directory, "Speakers", "failure.json")));
    }

    private sealed class FailingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new ProcessResult(1, "", "onnxruntime error", 1));
    }

    // ---- exports ---------------------------------------------------------------------------------------------------------------------

    private static FinalTranscript Transcript(bool speakers) => new(Guid.NewGuid(), "en",
    [
        new(1_000, 3_000, "Are you coming?", "Are you coming?", "", "agreement", Speaker: speakers ? "1" : null),
        new(3_000, 5_000, "Yes, at nine.", "Yes, at nine.", "", "agreement", Speaker: speakers ? "2" : null),
        new(6_000, 8_000, "Good.", "Good.", "", "agreement", Speaker: speakers ? "1" : null),
        new(8_000, 9_000, "See you.", "See you.", "", "agreement", Speaker: speakers ? "1" : null)
    ]);

    private async Task<string> ExportAsync(FinalTranscript transcript, string extension)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + extension);
        await TranscriptExporter.SaveAsync(transcript, path);
        return await File.ReadAllTextAsync(path);
    }

    [Fact]
    public async Task TheExportsSayWhoSpeaks()
    {
        var transcript = Transcript(speakers: true);
        Assert.Equal(string.Join(Environment.NewLine + Environment.NewLine, "Speaker 1: Are you coming?", "Speaker 2: Yes, at nine.", "Speaker 1: Good. See you."), await ExportAsync(transcript, ".txt"));
        Assert.Contains("00:00:03,000 --> 00:00:05,000\nSpeaker 2: Yes, at nine.", await ExportAsync(transcript, ".srt"));
        Assert.Contains("00:00:01.000 --> 00:00:03.000\n<v Speaker 1>Are you coming?", await ExportAsync(transcript, ".vtt"));
        var csv = await ExportAsync(transcript, ".csv");
        Assert.StartsWith("startMs,endMs,finalText,whisperText,canaryText,source,confidence,speaker\r\n", csv);
        Assert.EndsWith(",\"Speaker 1\"", csv.Split("\r\n")[1]);
        Assert.Contains("**00:00:03.000** · Speaker 2", await ExportAsync(transcript, ".md"));
        Assert.Contains("\"Speaker\": \"2\"", await ExportAsync(transcript, ".json"));
    }

    [Fact]
    public async Task ATranscriptWithoutSpeakersIsExportedExactlyAsBefore()
    {
        var transcript = Transcript(speakers: false);
        Assert.Equal(string.Join(Environment.NewLine + Environment.NewLine, "Are you coming?", "Yes, at nine.", "Good.", "See you."), await ExportAsync(transcript, ".txt"));
        Assert.StartsWith("startMs,endMs,finalText,whisperText,canaryText,source,confidence\r\n", await ExportAsync(transcript, ".csv"));
        Assert.Contains("00:00:01.000 --> 00:00:03.000\nAre you coming?", await ExportAsync(transcript, ".vtt"));
        Assert.DoesNotContain("Speaker", await ExportAsync(transcript, ".json"));
    }

    // ---- the API -----------------------------------------------------------------------------------------------------------------------

    private static ByteArrayContent Recording() => new(new byte[2_000]) { Headers = { { "Content-Type", "audio/wav" } } };

    private static string ErrorCode(string body) => JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetString()!;

    [Fact]
    public async Task ARecordingIsSentWithTheSpeakersToTellApartAndTheServerSaysWhetherItCan()
    {
        await using var api = await Harness.StartAsync(h => h.Speakers = true);
        Assert.True(JsonDocument.Parse(await api.Client.GetStringAsync("/v1/server")).RootElement.GetProperty("speakers").GetBoolean());
        using var auto = await api.Client.PostAsync("/v1/transcriptions?language=en&speakers=auto&name=a.wav", Recording());
        Assert.Equal(HttpStatusCode.Accepted, auto.StatusCode);
        using var three = await api.Client.PostAsync("/v1/transcriptions?language=en&speakers=3&name=b.wav", Recording());
        using var plain = await api.Client.PostAsync("/v1/transcriptions?language=en&name=c.wav", Recording());
        var asked = api.Workspace.Options.Values.Select(options => options.Speakers).Order().ToArray();
        Assert.Equal(["3", "auto"], asked);                                                              // a recording without the choice keeps no options at all
        using var wrong = await api.Client.PostAsync("/v1/transcriptions?language=en&speakers=lots&name=d.wav", Recording());
        Assert.Equal("invalid_speakers", ErrorCode(await wrong.Content.ReadAsStringAsync()));

        // The Client library sends the choice only when there is one, so an older server is asked nothing new.
        using var client = new RemoteServerClient(api.Client.BaseAddress!, ApiTestData.Key, null);
        var path = Path.Combine(api.Root, "talk.wav");
        await File.WriteAllBytesAsync(path, new byte[2_000]);
        var sent = await client.UploadAsync(path, "en", null, default, "4");
        Assert.Equal("4", api.Workspace.Options[sent.Id].Speakers);
        var unasked = await client.UploadAsync(path, "en", null, default);
        Assert.False(api.Workspace.Options.ContainsKey(unasked.Id));
    }

    [Fact]
    public async Task AServerWithoutTheSpeakerProgramRefusesToTellSpeakersApartBeforeTakingTheRecording()
    {
        await using var api = await Harness.StartAsync();
        Assert.False(JsonDocument.Parse(await api.Client.GetStringAsync("/v1/server")).RootElement.GetProperty("speakers").GetBoolean());
        using var refused = await api.Client.PostAsync("/v1/transcriptions?language=en&speakers=auto&name=a.wav", Recording());
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("speakers_unavailable", ErrorCode(await refused.Content.ReadAsStringAsync()));
        Assert.Empty(api.Repository.Jobs);
        using var plain = await api.Client.PostAsync("/v1/transcriptions?language=en&speakers=off&name=a.wav", Recording());
        Assert.Equal(HttpStatusCode.Accepted, plain.StatusCode);
    }

    [Fact]
    public async Task ALinkTakesTheSpeakersToTellApartToTheJobMadeWhenItsSoundArrives()
    {
        var links = new FakeLinks();
        await using var api = await Harness.StartAsync(h => { h.Speakers = true; h.Links = links; });
        using var client = new RemoteServerClient(api.Client.BaseAddress!, ApiTestData.Key, null);
        var job = await client.SendLinkAsync("https://media.example/talk.mp3", "en", default, "auto");
        for (var i = 0; i < 100 && !api.Workspace.Options.ContainsKey(job.Id); i++) await Task.Delay(50);
        Assert.Equal("auto", api.Workspace.Options[job.Id].Speakers);
    }
}
