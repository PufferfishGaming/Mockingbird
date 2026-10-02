using System.IO;
using TriAsr.Application;
using TriAsr.Engine.Whisper;

namespace TriAsr.Engine.Tests;

public sealed class WhisperSkipArgumentTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _executable;
    public WhisperSkipArgumentTests()
    {
        Directory.CreateDirectory(_directory);
        _executable = Path.Combine(_directory, "whisper-cli.exe");
        File.WriteAllText(_executable, "stand-in");
    }
    public void Dispose() { try { Directory.Delete(_directory, true); } catch (IOException) { } }

    private sealed class Runner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var arguments = request.Arguments.ToList();
            if (arguments.Contains("-ojf"))
                await File.WriteAllTextAsync(arguments[arguments.IndexOf("-of") + 1] + ".json",
                    """{"result":{"language":"de"},"transcription":[{"offsets":{"from":4500,"to":6000},"text":" Hallo Welt"}]}""", cancellationToken);
            return new(0, "", arguments.Contains("-dl") ? "whisper_full_with_state: auto-detected language: de (p = 0.970000)" : "", 1);
        }
    }

    private WhisperEngine Engine(Runner runner) => new(runner, _executable, "model.bin", 4);

    [Fact]
    public async Task WithoutASkipModelWhisperIsNotToldToUseTheDetector()
    {
        var runner = new Runner();
        await Engine(runner).TranscribeAsync("a.wav", 10, "de", Path.Combine(_directory, "w"), "cpu", default);
        Assert.DoesNotContain("--vad", runner.Requests[0].Arguments);
    }

    [Fact]
    public async Task WithASkipModelWhisperSkipsWithTheSameThresholdsAsThePlan()
    {
        var runner = new Runner();
        var transcript = await Engine(runner).TranscribeAsync("a.wav", 10, "de", Path.Combine(_directory, "w"), "cpu", default, vadModel: @"C:\app\Runtimes\Vad\silero.bin");
        var arguments = runner.Requests[0].Arguments.ToList();
        var at = arguments.IndexOf("--vad");
        Assert.True(at >= 0);
        Assert.Equal(["--vad", "-vm", @"C:\app\Runtimes\Vad\silero.bin", .. VadSegmenter.Thresholds], arguments.Skip(at).Take(3 + VadSegmenter.Thresholds.Count));
        Assert.Equal(4_500, transcript.Segments[0].StartMs); // times stay on the recording's own timeline
    }

    [Fact]
    public async Task LanguageSamplesAreTakenWhereTheCallerSays()
    {
        var runner = new Runner();
        var detection = await Engine(runner).DetectLanguageAsync("a.wav", 600, Path.Combine(_directory, "lang"), "cpu", "ffmpeg.exe", default, [12.5, 200, 305.25]);
        var starts = runner.Requests.Where(request => request.Executable == "ffmpeg.exe").Select(request => request.Arguments[request.Arguments.ToList().IndexOf("-ss") + 1]);
        Assert.Equal(["12.5", "200", "305.25"], starts);
        Assert.Equal("de", detection.Language);
    }

    [Fact]
    public async Task WithoutSampleStartsTheOldPositionsAreKept()
    {
        var runner = new Runner();
        await Engine(runner).DetectLanguageAsync("a.wav", 600, Path.Combine(_directory, "lang"), "cpu", "ffmpeg.exe", default);
        var starts = runner.Requests.Where(request => request.Executable == "ffmpeg.exe").Select(request => request.Arguments[request.Arguments.ToList().IndexOf("-ss") + 1]);
        Assert.Equal(["0", "292.5", "585"], starts);
    }
}
