using System.IO;
using System.Text.Json;
using TriAsr.App;
using TriAsr.Application;
using TriAsr.Domain;
using TriAsr.Engine.Canary;
using TriAsr.Engine.Whisper;
using TriAsr.Infrastructure;

namespace TriAsr.Ui.SmokeTests;

/// <summary>
/// A recording in two languages (language <c>en+hu</c>) through the stages, with stand-ins for the speech programs: the language of each speech stretch,
/// Whisper block by block, Canary language by language, and a recording that turns out to be in one language going the usual way.
/// </summary>
public sealed class LanguagePairStageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
    private readonly Guid _job = Guid.NewGuid();
    private readonly RuntimePaths _paths;
    private readonly StoragePaths _storage;
    private readonly JobWorkspace _workspace;
    private readonly List<(string Title, string Message)> _issues = [];
    private string Directory => _workspace.DirectoryFor(_job);

    public LanguagePairStageTests()
    {
        _storage = new(Path.Combine(_root, "data")); _storage.EnsureDirectories(); _workspace = new(_storage);
        _paths = new RuntimePaths(Path.Combine(_root, "app")) { CanaryModel = Path.Combine(_root, "canary.gguf"), CorrectionModel = Path.Combine(_root, "absent", "correction.gguf") };
        foreach (var file in new[] { _paths.WhisperFor("cpu"), _paths.CanaryModel }) { System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "stand-in"); }
        System.IO.Directory.CreateDirectory(Directory);
        WriteSilence(Path.Combine(Directory, "normalized.wav"), 40);
        File.WriteAllText(Path.Combine(Directory, "configuration.json"), JsonSerializer.Serialize(new JobConfiguration("test", "cpu", 1, "cpu", 1, "cpu", 1, false, false, false)));
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

    // Quiet, speech (chunk 1), quiet, speech (chunk 3), quiet.
    private void SavePlan() => File.WriteAllText(Path.Combine(Directory, "chunks.json"), JsonSerializer.Serialize(new ChunkPlan(ChunkPlan.CurrentVersion, 40_000, "test",
        [new(0, 0, 9_800, false, false), new(1, 9_800, 14_200, true, false), new(2, 14_200, 29_800, false, false), new(3, 29_800, 34_200, true, false), new(4, 34_200, 40_000, false, false)])));

    private TranscriptionJob Job(string language) => new(_job, "source.wav", language, JobState.Queued, DateTimeOffset.UtcNow);

    private LocalTranscriptionStages Stages(IProcessRunner runner)
    {
        var stages = new LocalTranscriptionStages(_workspace, new TriAsr.Audio.FfmpegNormalizer(runner, "unused"), runner, _paths, _storage, new ModelStore(_root),
            new Records(), new TriAsr.Hardware.ResourceGovernor(() => 24, () => TriAsr.Hardware.PowerSource.Ac),
            new SettingsStore(_storage, Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsStore>.Instance));
        stages.IssueOccurred += (_, issue) => _issues.Add(issue);
        return stages;
    }

    /// <summary>
    /// Stands in for whisper-cli and Canary. Detecting, it names the language of each stretch file (by its chunk number); reading a stretch in a language, it is
    /// sure of its words only in the language the stretch is in; transcribing a block, it writes one segment a second into the block; Canary writes one segment a window.
    /// </summary>
    private sealed class Runner(Dictionary<int, (string Language, double Detection)> detected, Dictionary<int, string> spoken) : IProcessRunner
    {
        public List<List<string>> Whisper { get; } = [];
        public List<CanaryRequest> Canary { get; } = [];
        public Dictionary<string, long> BlockBytes { get; } = [];

        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            var arguments = request.Arguments.ToList();
            if (arguments.Contains("--canary"))
            {
                var input = JsonSerializer.Deserialize<CanaryRequest>(await File.ReadAllTextAsync(arguments[1], cancellationToken))!;
                Canary.Add(input);
                var segments = input.Windows!.Select(window => new TranscriptSegment(window.StartMs, window.EndMs, input.Language + " canary")).ToArray();
                var transcript = new EngineTranscript("Canary", "model", "runtime", input.Backend, input.Backend, "CPU", input.Language, 40, 1, segments, string.Join(" ", segments.Select(segment => segment.Text)), false);
                await File.WriteAllTextAsync(input.Output, JsonSerializer.Serialize(new CanaryNative.Result(transcript, ["raw"], input.Backend)), cancellationToken);
                return new(0, "", "", 1);
            }
            Whisper.Add(arguments);
            var language = arguments[arguments.IndexOf("-l") + 1];
            var said = new System.Text.StringBuilder();
            var files = arguments.Select((argument, i) => (argument, i)).Where(item => item.argument == "-f").Select(item => arguments[item.i + 1]).ToList();
            foreach (var file in files)
            {
                said.AppendLine($"main: processing '{file}' (1000 samples, 1.0 sec), 1 threads, 1 processors, lang = {language} ...");
                var chunk = int.TryParse(Path.GetFileNameWithoutExtension(file), out var number) ? number : -1;
                if (arguments.Contains("-dl")) said.AppendLine($"whisper_full_with_state: auto-detected language: {detected[chunk].Language} (p = {detected[chunk].Detection.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
                else if (arguments.Contains("-np"))
                {
                    var output = arguments[arguments.IndexOf(file) + 2];
                    var sure = spoken[chunk] == language ? 0.95 : 0.4;
                    await File.WriteAllTextAsync(output + ".json", $$"""{"result":{"language":"{{language}}"},"transcription":[{"text":" {{language}} words","tokens":[{"text":" w","p":{{sure.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}]}]}""", cancellationToken);
                }
                else
                {
                    BlockBytes[language] = new FileInfo(file).Length;
                    var seconds = (new FileInfo(file).Length - 44) / 32_000;
                    var segments = string.Join(",", Enumerable.Range(0, (int)seconds).Select(s => $$"""{"offsets":{"from":{{s * 1000}},"to":{{s * 1000 + 900}}},"text":" {{language}} {{s}}"}"""));
                    await File.WriteAllTextAsync(arguments[arguments.IndexOf("-of") + 1] + ".json", $$"""{"result":{"language":"{{language}}"},"transcription":[{{segments}}]}""", cancellationToken);
                }
            }
            return new(0, "", said.ToString(), 1);
        }
    }

    private T Read<T>(string name) => JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(Directory, name)))!;

    private sealed class Records : IRecordRepository
    {
        public Task SaveAsync(WorkspaceRecord record, CancellationToken token = default) => Task.CompletedTask;
        public Task<IReadOnlyList<WorkspaceRecord>> ListAsync(string kind, Guid? jobId = null, CancellationToken token = default) => Task.FromResult<IReadOnlyList<WorkspaceRecord>>([]);
    }

    [Fact]
    public async Task EachSpeechStretchGetsItsLanguageAndTheRecordingIsDividedIntoBlocks()
    {
        SavePlan();
        // Chunk 1 is Hungarian and the program is sure of it; chunk 3 is English that the program heard as Hungarian, as it did with an accent.
        var runner = new Runner(new() { [1] = ("hu", 0.998), [3] = ("hu", 0.92) }, new() { [1] = "hu", [3] = "en" });
        await Stages(runner).ExecuteAsync(Job("en+hu"), JobState.DetectingLanguage, default);

        var detection = Read<WhisperEngine.LanguageDetection>("language.json");
        Assert.Equal("en+hu", detection.Language);
        Assert.Equal(["hu", "en"], detection.WindowLanguages);
        Assert.Equal([new LanguageBlock(0, 22_000, "hu"), new LanguageBlock(22_000, 40_000, "en")], detection.Blocks);
        Assert.Equal(3, runner.Whisper.Count);                                                  // one run detects both stretches; only the unsure one is read, once in each language
        Assert.Contains("-dl", runner.Whisper[0]);
        Assert.Equal(2, runner.Whisper[0].Count(argument => argument == "-f"));
        Assert.Equal(["en", "hu"], runner.Whisper.Skip(1).Select(run => run[run.IndexOf("-l") + 1]));
        Assert.All(runner.Whisper.Skip(1), run => Assert.Single(run, argument => argument == "-f"));
        Assert.True(File.Exists(Path.Combine(Directory, "Language", "pair.json")));             // what was decided, and why, is kept
        Assert.Empty(System.IO.Directory.GetFiles(Path.Combine(Directory, "Language", "Pair"), "*.wav"));
        Assert.Empty(_issues);
    }

    [Fact]
    public async Task ARecordingThatTurnsOutToBeInOneLanguageGoesExactlyAsIfThatLanguageHadBeenChosen()
    {
        SavePlan();
        var runner = new Runner(new() { [1] = ("hu", 0.999), [3] = ("hu", 0.995) }, new() { [1] = "hu", [3] = "hu" });
        await Stages(runner).ExecuteAsync(Job("en+hu"), JobState.DetectingLanguage, default);
        var detection = Read<WhisperEngine.LanguageDetection>("language.json");
        Assert.Equal("hu", detection.Language);
        Assert.Null(detection.Blocks);
        Assert.Single(runner.Whisper);                                                          // sure of both: nothing is read twice

        await Stages(runner).ExecuteAsync(Job("en+hu"), JobState.RunningWhisper, default);
        var whole = runner.Whisper[^1];
        Assert.Equal(Path.Combine(Directory, "normalized.wav"), Path.GetFullPath(whole[whole.IndexOf("-f") + 1]));   // the whole recording, in Hungarian, as before
        Assert.Equal("hu", whole[whole.IndexOf("-l") + 1]);
    }

    private void SaveBlocks() => File.WriteAllText(Path.Combine(Directory, "language.json"), JsonSerializer.Serialize(
        new WhisperEngine.LanguageDetection("en+hu", 1, ["hu", "en"], [new LanguageBlock(0, 22_000, "hu"), new LanguageBlock(22_000, 40_000, "en")])));

    [Fact]
    public async Task WhisperReadsEachBlockInItsLanguageAndTheSegmentsGoBackOnTheRecordingsTimeline()
    {
        SavePlan(); SaveBlocks();
        var runner = new Runner([], []);
        await Stages(runner).ExecuteAsync(Job("en+hu"), JobState.RunningWhisper, default);
        Assert.Equal(["hu", "en"], runner.Whisper.Select(run => run[run.IndexOf("-l") + 1]));
        Assert.Equal(44 + 22 * 32_000, runner.BlockBytes["hu"]);
        Assert.Equal(44 + 18 * 32_000, runner.BlockBytes["en"]);
        var whisper = Read<EngineTranscript>("whisper.json");
        Assert.Equal("en+hu", whisper.Language);
        Assert.Equal(40, whisper.Segments.Count);
        Assert.Equal(new TranscriptSegment(21_000, 21_900, "hu 21"), whisper.Segments[21]);
        Assert.Equal(new TranscriptSegment(22_000, 22_900, "en 0"), whisper.Segments[22]);
        Assert.Equal(new TranscriptSegment(39_000, 39_900, "en 17"), whisper.Segments[^1]);
        Assert.True(File.Exists(Path.Combine(Directory, "Whisper", "Block001", "raw.json")));     // each block's own output is kept
        Assert.False(File.Exists(Path.Combine(Directory, "Whisper", "Block001", "block.wav")));
    }

    [Fact]
    public async Task CanaryReadsTheSpeechOfEachLanguageInThatLanguageAndBothReadingsArePutBackInOrder()
    {
        SavePlan(); SaveBlocks();
        var runner = new Runner([], []);
        await Stages(runner).ExecuteAsync(Job("en+hu"), JobState.RunningCanary, default);
        Assert.Equal(["en", "hu"], runner.Canary.Select(request => request.Language));
        Assert.Equal([3], runner.Canary[0].Windows!.Select(window => window.Index));
        Assert.Equal([1], runner.Canary[1].Windows!.Select(window => window.Index));
        Assert.Equal(Path.Combine(Directory, "Canary", "hu", "windows"), Path.GetFullPath(runner.Canary[1].CheckpointDirectory!));
        var canary = Read<CanaryNative.Result>("canary.json").Transcript;
        Assert.Equal("en+hu", canary.Language);
        Assert.Equal("hu canary en canary", canary.Text);
    }

    [Fact]
    public async Task APairWithALanguageCanaryDoesNotKnowIsReadByWhisperAloneAndMarkedForListening()
    {
        SavePlan();
        File.WriteAllText(Path.Combine(Directory, "language.json"), JsonSerializer.Serialize(
            new WhisperEngine.LanguageDetection("ja+en", 1, ["ja", "en"], [new LanguageBlock(0, 22_000, "ja"), new LanguageBlock(22_000, 40_000, "en")])));
        var runner = new Runner([], []);
        await Stages(runner).ExecuteAsync(Job("ja+en"), JobState.RunningCanary, default);
        Assert.Empty(runner.Canary);
        Assert.True(File.Exists(Path.Combine(Directory, "Canary", "skipped-language.json")));
    }

    [Fact]
    public async Task WithoutTheSpeechDetectorsPlanTheRecordingIsCutIntoEvenStretches()
    {
        // 40 s cut every 20 s, overlapping by a second: stretches 0-20 s, 19-39 s and 38-40 s.
        var runner = new Runner(new() { [0] = ("hu", 0.999), [1] = ("en", 0.999), [2] = ("en", 0.999) }, new() { [0] = "hu", [1] = "en", [2] = "en" });
        await Stages(runner).ExecuteAsync(Job("en+hu"), JobState.DetectingLanguage, default);
        var detection = Read<WhisperEngine.LanguageDetection>("language.json");
        Assert.Equal("en+hu", detection.Language);
        Assert.Equal(2, detection.Blocks!.Count);
        Assert.Equal("even", JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory, "Language", "pair.json"))).RootElement.GetProperty("Plan").GetString());
    }
}
