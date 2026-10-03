using System.IO;
using System.Text.Json;
using TriAsr.Application;
using TriAsr.Domain;
using TriAsr.Engine.Whisper;

namespace TriAsr.Engine.Tests;

/// <summary>The speaker program (sherpa-onnx speaker diarization): what it is given, what is read from it, and the words of Whisper's output with their times.</summary>
public sealed class SpeakerFinderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));

    public SpeakerFinderTests() => Directory.CreateDirectory(_directory);
    public void Dispose() { try { Directory.Delete(_directory, true); } catch (IOException) { } }

    // What the program prints (the configuration line, then a turn per line, then how long it took).
    private const string Output = """
        OfflineSpeakerDiarizationConfig(segmentation=..., clustering=FastClusteringConfig(num_clusters=-1, threshold=1.05))
        Started
        10.882 -- 14.965 speaker_01
        17.868 -- 18.492 speaker_02
        18.813 -- 20.365 speaker_01
        Duration : 1049.355 s
        Elapsed seconds: 51.002 s
        Real time factor (RTF): 51.002 / 1049.355 = 0.049
        """;

    [Fact]
    public void EachTurnIsReadInMilliseconds()
    {
        Assert.Equal([new SpeakerTurn(10_882, 14_965, "speaker_01"), new SpeakerTurn(17_868, 18_492, "speaker_02"), new SpeakerTurn(18_813, 20_365, "speaker_01")], SpeakerFinder.Parse(Output));
        Assert.Empty(SpeakerFinder.Parse("Started\nElapsed seconds: 0.4 s"));                         // silence: finished, nobody spoke
    }

    [Fact]
    public void OutputThatDoesNotSayItFinishedOrMakesNoSenseIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => SpeakerFinder.Parse("Started\n10.882 -- 14.965 speaker_01"));
        Assert.Throws<InvalidDataException>(() => SpeakerFinder.Parse("5.000 -- 4.000 speaker_00\nElapsed seconds: 1 s"));
    }

    private sealed class Runner(string output, int exitCode = 0) : IProcessRunner
    {
        public ProcessRequest? Request { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            request.ErrorLine?.Invoke("progress 41.50%");
            return Task.FromResult(new ProcessResult(exitCode, output, "", 1));
        }
    }

    [Fact]
    public async Task TheProgramIsGivenBothModelsTheThreadsAndTheMeasuredThreshold()
    {
        var runner = new Runner(Output);
        var progress = new List<double>();
        var turns = await SpeakerFinder.FindAsync(runner, @"C:\app\Runtimes\Speakers\diarize.exe", "seg.onnx", "titanet.onnx", "normalized.wav", 6, default, progress.Add);
        Assert.Equal(3, turns.Count);
        Assert.Equal(["--segmentation.pyannote-model=seg.onnx", "--embedding.model=titanet.onnx", "--segmentation.num-threads=6", "--embedding.num-threads=6", "--clustering.cluster-threshold=1.05", "normalized.wav"], runner.Request!.Arguments);
        Assert.Equal([0.415], progress);
        var failed = new Runner("", exitCode: 3);
        Assert.Contains("Speaker detection failed.", (await Assert.ThrowsAsync<InvalidOperationException>(() => SpeakerFinder.FindAsync(failed, "x.exe", "s", "e", "a.wav", 1, default))).Message);
    }

    [Fact]
    public void WhispersWordsAreItsTokensJoinedAtTheSpacesWithTheirTimesMoved()
    {
        var json = """
            {"transcription":[{"offsets":{"from":0,"to":2000},"text":" Hello there.","tokens":[
              {"text":"[_BEG_]","offsets":{"from":0,"to":0}},{"text":" Hel","offsets":{"from":100,"to":300}},{"text":"lo","offsets":{"from":300,"to":500}},
              {"text":" there","offsets":{"from":600,"to":900}},{"text":".","offsets":{"from":900,"to":950}},{"text":"[_TT_100]","offsets":{"from":2000,"to":2000}}]}]}
            """;
        using var document = JsonDocument.Parse(json);
        Assert.Equal([new TimedWord(10_100, 10_500, " Hello"), new TimedWord(10_600, 10_950, " there.")], WhisperEngine.ParseWords(document.RootElement, 10_000));
    }

    // ---- the real program -------------------------------------------------------------------------------------------------------------

    private static string? FindFolder()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var folder = Path.Combine(directory.FullName, "Runtimes", "Speakers");
            if (File.Exists(Path.Combine(folder, "sherpa-onnx-offline-speaker-diarization.exe")) && File.Exists(Path.Combine(folder, "nemo_en_titanet_small.onnx"))) return folder;
        }
        return null;
    }

    private sealed class SpeakersFactAttribute : FactAttribute
    {
        public SpeakersFactAttribute() { if (FindFolder() is null) Skip = "No speaker program found: pending on a machine with Runtimes/Speakers."; }
    }

    private sealed class RealRunner : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            var info = new System.Diagnostics.ProcessStartInfo(request.Executable) { WorkingDirectory = request.WorkingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in request.Arguments) info.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken); var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new(process.ExitCode, await output, await error, 0);
        }
    }

    [SpeakersFact]
    public async Task TheRealProgramTakesTheOptionsAndReportsAResult()
    {
        var folder = FindFolder()!;
        var wave = Path.Combine(_directory, "two tones.wav");                                               // a space in the path, as a user folder can have
        var samples = new byte[6 * 32_000];
        for (var i = 0; i < samples.Length / 2; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(i * 2), (short)(Math.Sin(2 * Math.PI * (i < samples.Length / 4 ? 180 : 260) * i / 16_000) * 4000));
        File.WriteAllBytes(wave, PhraseText.Wav(samples));
        // Tones are not voices; whatever it makes of them, it must take every option and say that it finished.
        var turns = await SpeakerFinder.FindAsync(new RealRunner(), Path.Combine(folder, "sherpa-onnx-offline-speaker-diarization.exe"), Path.Combine(folder, "pyannote-segmentation-3.0.onnx"),
            Path.Combine(folder, "nemo_en_titanet_small.onnx"), wave, 2, default);
        Assert.All(turns, turn => Assert.InRange(turn.EndMs, turn.StartMs, 6_500));
    }
}
