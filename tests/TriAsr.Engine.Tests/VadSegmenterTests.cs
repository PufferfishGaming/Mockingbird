using TriAsr.Application;
using TriAsr.Domain;
using TriAsr.Engine.Whisper;

namespace TriAsr.Engine.Tests;

public sealed class VadSegmenterTests
{
    // Taken from a real run of whisper-vad-speech-segments.exe on a recording: times are in hundredths of a second.
    private const string RealOutput = "\nDetected 3 speech segments:\nSpeech segment 0: start = 227.00, end = 1005.00\nSpeech segment 1: start = 1795.00, end = 2633.00\nSpeech segment 2: start = 168717.00, end = 168816.00\n";

    [Fact]
    public void TimesAreConvertedFromHundredthsOfASecondToMilliseconds()
    {
        var spans = VadSegmenter.Parse(RealOutput);
        Assert.Equal([new SpeechSpan(2_270, 10_050), new SpeechSpan(17_950, 26_330), new SpeechSpan(1_687_170, 1_688_160)], spans);
    }

    [Fact]
    public void NoSpeechIsAValidResult() => Assert.Empty(VadSegmenter.Parse("\nDetected 0 speech segments:\n"));

    [Fact]
    public void ChangedOrIncompleteOutputFailsInsteadOfProducingAWrongPlan()
    {
        Assert.Throws<InvalidDataException>(() => VadSegmenter.Parse(""));
        Assert.Throws<InvalidDataException>(() => VadSegmenter.Parse("something else entirely"));
        Assert.Throws<InvalidDataException>(() => VadSegmenter.Parse("Detected 2 speech segments:\nSpeech segment 0: start = 1.00, end = 2.00\n"));
        Assert.Throws<InvalidDataException>(() => VadSegmenter.Parse("Detected 1 speech segments:\nSpeech segment 0: start = 9.00, end = 2.00\n"));
    }

    [Fact]
    public async Task TheToolIsStartedWithTheAudioModelAndThreadsAndItsOutputIsParsed()
    {
        var runner = new FakeRunner(new(0, RealOutput, "ggml_vulkan: Found 1 Vulkan devices", 1));
        var spans = await VadSegmenter.DetectAsync(runner, @"C:\app\Runtimes\Whisper-Vulkan\whisper-vad-speech-segments.exe", @"C:\app\Runtimes\Vad\silero.bin", @"C:\job\normalized.wav", 4, default);
        Assert.Equal(3, spans.Count);
        var request = Assert.Single(runner.Requests);
        Assert.Equal(["-f", @"C:\job\normalized.wav", "-vm", @"C:\app\Runtimes\Vad\silero.bin", "-t", "4", "--vad-threshold", "0.5", "--vad-min-speech-duration-ms", "250", "--vad-min-silence-duration-ms", "100", "--vad-speech-pad-ms", "30", "-np"], request.Arguments);
        Assert.Equal(@"C:\app\Runtimes\Whisper-Vulkan", request.WorkingDirectory);
    }

    [Fact]
    public async Task AFailedRunExplainsWhyWithoutDumpingTheWholeLog()
    {
        var runner = new FakeRunner(new(1, "", new string('x', 5_000) + "model file not found", 1));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => VadSegmenter.DetectAsync(runner, "tool.exe", "m.bin", "a.wav", 2, default));
        Assert.Contains("model file not found", error.Message);
        Assert.True(error.Message.Length < 400);
    }

    // ---- the real tool: a fake runner cannot know which options the program really accepts ----------------------------------------

    private static (string Tool, string Model)? FindDetector()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var tool = Path.Combine(directory.FullName, "Runtimes", "Whisper-Vulkan", "whisper-vad-speech-segments.exe");
            var model = Path.Combine(directory.FullName, "Runtimes", "Vad", "ggml-silero-v5.1.2.bin");
            if (File.Exists(tool) && File.Exists(model)) return (tool, model);
        }
        return null;
    }

    private sealed class DetectorFactAttribute : FactAttribute
    {
        public DetectorFactAttribute() { if (FindDetector() is null) Skip = "No speech detector found: pending on a machine with Runtimes/Whisper-Vulkan/whisper-vad-speech-segments.exe and Runtimes/Vad."; }
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

    [DetectorFact]
    public async Task TheRealDetectorAcceptsEveryOptionTheAppPassesAndReportsAResult()
    {
        var (tool, model) = FindDetector()!.Value;
        var wave = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N") + ".wav");
        Directory.CreateDirectory(Path.GetDirectoryName(wave)!);
        try
        {
            var bytes = 3 * 32_000;
            using (var writer = new BinaryWriter(File.Create(wave)))
            {
                writer.Write("RIFF"u8); writer.Write(36 + bytes); writer.Write("WAVEfmt "u8); writer.Write(16);
                writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
                writer.Write("data"u8); writer.Write(bytes); writer.Write(new byte[bytes]);
            }
            // It exits with 0 even when it rejects an option, so the only proof is that it announces a result (here: no speech in silence).
            Assert.Empty(await VadSegmenter.DetectAsync(new RealRunner(), tool, model, wave, 2, default));
        }
        finally { File.Delete(wave); }
    }

    private sealed class FakeRunner(ProcessResult result) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default) { Requests.Add(request); return Task.FromResult(result); }
    }
}
