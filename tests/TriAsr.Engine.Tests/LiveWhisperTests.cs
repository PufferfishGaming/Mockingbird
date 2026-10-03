using System.IO;
using TriAsr.Application;
using TriAsr.Engine.Whisper;

namespace TriAsr.Engine.Tests;

/// <summary>Reading a phrase of live dictation with whisper-cli: the runs it makes for one or two languages, and what it reads from the program's output.</summary>
public sealed class LiveWhisperTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _executable, _model;

    public LiveWhisperTests()
    {
        Directory.CreateDirectory(_directory);
        _executable = Path.Combine(_directory, "whisper-cli.exe");
        _model = Path.Combine(_directory, "model.bin");
        File.WriteAllText(_executable, "stand-in");
        File.WriteAllText(_model, "stand-in");
    }

    public void Dispose() { try { Directory.Delete(_directory, true); } catch (IOException) { } }

    /// <summary>What whisper-cli writes for a phrase read in one language: the text, and each token with its probability.</summary>
    private static string Json(string language, string text, params double[] probabilities)
    {
        var tokens = probabilities.Select((p, i) => "{\"text\":\" w" + i + "\",\"p\":" + p.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}").Prepend("{\"text\":\"[_BEG_]\",\"p\":0.5}");
        return "{\"result\":{\"language\":\"" + language + "\"},\"transcription\":[{\"text\":\" " + text + "\",\"tokens\":[" + string.Join(",", tokens) + "]}]}";
    }

    /// <summary>Answers each language the way the test says, as the program would: the JSON file, and on detection the line that names the language.</summary>
    private sealed class Runner(Dictionary<string, (string Json, string Said)> answers) : IProcessRunner
    {
        public List<List<string>> Runs { get; } = [];
        public int ExitCode { get; set; }

        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            var arguments = request.Arguments.ToList();
            Runs.Add(arguments);
            Assert.True(File.Exists(arguments[arguments.IndexOf("-f") + 1]));                  // the phrase is on disk while it is read
            var (json, said) = answers[arguments[arguments.IndexOf("-l") + 1]];
            await File.WriteAllTextAsync(arguments[arguments.IndexOf("-of") + 1] + ".json", json, cancellationToken);
            return new(ExitCode, "", said, 1);
        }
    }

    private LiveWhisper Whisper(Runner runner) => new(runner, _executable, _model, 4, "vulkan", Path.Combine(_directory, "live"));

    private static byte[] Wav() => PhraseText.Wav(new byte[32_000]);

    [Fact]
    public async Task AccentedEnglishHeardAsHungarianIsReadInEnglishTooAndTheEnglishReadingWins()
    {
        // The measured case: whisper-cli detected Hungarian at 0.92 and wrote a Hungarian translation it was unsure of; read in English the words came with confidence.
        var runner = new Runner(new()
        {
            ["auto"] = (Json("hu", "A szulfur a 10. legnagyobb elem.", 0.5, 0.4, 0.45), "whisper_full_with_state: auto-detected language: hu (p = 0.923000)"),
            ["en"] = (Json("en", "Sulfur is the tenth most abundant element.", 0.95, 0.9, 0.97), "")
        });
        var phrase = await Whisper(runner).RecognizeAsync(Wav(), "en+hu", null, default);
        Assert.Equal(new LivePhrase("Sulfur is the tenth most abundant element.", "en"), phrase);
        Assert.Equal(2, runner.Runs.Count);
        Assert.Equal("auto", runner.Runs[0][runner.Runs[0].IndexOf("-l") + 1]);
        Assert.DoesNotContain("-np", runner.Runs[0]);                                          // detecting, the program must say how sure it is
        Assert.Contains("-np", runner.Runs[1]);
        Assert.All(runner.Runs, run => Assert.Contains("-ojf", run));
        Assert.Equal(runner.Runs[0][runner.Runs[0].IndexOf("-f") + 1], runner.Runs[1][runner.Runs[1].IndexOf("-f") + 1]);   // the phrase is written once
        Assert.Empty(Directory.GetFiles(Path.Combine(_directory, "live")));                    // and nothing is left behind
    }

    [Fact]
    public async Task OneLanguageOrAutoDetectIsOneRunAsBefore()
    {
        var runner = new Runner(new()
        {
            ["auto"] = (Json("hu", "Jó napot.", 0.9), "auto-detected language: hu (p = 0.998000)"),
            ["de"] = (Json("de", "Guten Tag.", 0.9), "")
        });
        Assert.Equal(new LivePhrase("Jó napot.", "hu"), await Whisper(runner).RecognizeAsync(Wav(), "auto", null, default));
        Assert.Equal(new LivePhrase("Guten Tag.", "de"), await Whisper(runner).RecognizeAsync(Wav(), "de", "hu", default));
        Assert.Equal(2, runner.Runs.Count);
        Assert.Contains("-np", runner.Runs[1]);
    }

    [Fact]
    public async Task AProgramThatFailsOrWritesNothingIsAPhraseThatCouldNotBeRead()
    {
        var runner = new Runner(new() { ["en"] = ("not json", "") });
        Assert.Equal(LiveMessages.Failed, (await Assert.ThrowsAsync<LiveException>(() => Whisper(runner).RecognizeAsync(Wav(), "en", null, default))).Message);
        runner.ExitCode = 1;
        Assert.Equal(LiveMessages.Failed, (await Assert.ThrowsAsync<LiveException>(() => Whisper(runner).RecognizeAsync(Wav(), "en", null, default))).Message);
        File.Delete(_model);
        Assert.Equal(LiveMessages.NoModel, (await Assert.ThrowsAsync<LiveException>(() => Whisper(runner).RecognizeAsync(Wav(), "en", null, default))).Message);
    }

    // ---- the real program -------------------------------------------------------------------------------------------------------------

    private static (string Tool, string Model)? FindWhisper()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var tool = Path.Combine(directory.FullName, "Runtimes", "Whisper-Vulkan", "whisper-cli.exe");
            var models = Path.Combine(directory.FullName, "Models", "Whisper");
            if (File.Exists(tool) && Directory.Exists(models) && Directory.GetFiles(models, "ggml-*.bin").OrderBy(path => new FileInfo(path).Length).FirstOrDefault() is { } model) return (tool, model);
        }
        return null;
    }

    private sealed class WhisperFactAttribute : FactAttribute
    {
        public WhisperFactAttribute() { if (FindWhisper() is null) Skip = "No speech program found: pending on a machine with Runtimes/Whisper-Vulkan/whisper-cli.exe and a model in Models/Whisper."; }
    }

    /// <summary>Runs the real program, and keeps what it wrote and said before the reader deletes it.</summary>
    private sealed class RealRunner : IProcessRunner
    {
        public List<(string Language, string Json, string Said)> Runs { get; } = [];

        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            var info = new System.Diagnostics.ProcessStartInfo(request.Executable) { WorkingDirectory = request.WorkingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in request.Arguments) info.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken); var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var arguments = request.Arguments.ToList();
            var json = arguments[arguments.IndexOf("-of") + 1] + ".json";
            Runs.Add((arguments[arguments.IndexOf("-l") + 1], File.Exists(json) ? await File.ReadAllTextAsync(json, cancellationToken) : "", await error));
            return new(process.ExitCode, await output, await error, 0);
        }
    }

    [WhisperFact]
    public async Task TheRealProgramTakesTheOptionsAndSaysWhichLanguageItHeardAndHowSure()
    {
        var (tool, model) = FindWhisper()!.Value;
        var runner = new RealRunner();
        var whisper = new LiveWhisper(runner, tool, model, 4, "vulkan", Path.Combine(_directory, "real"));
        var tone = new byte[2 * 32_000];
        for (var i = 0; i < tone.Length / 2; i++) System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(tone.AsSpan(i * 2), (short)(Math.Sin(2 * Math.PI * 220 * i / 16_000) * 3000));
        await whisper.RecognizeAsync(PhraseText.Wav(tone), "en+hu", null, default);             // a tone: whatever it makes of it, both runs must work
        Assert.NotEmpty(runner.Runs);
        Assert.All(runner.Runs, run => Assert.StartsWith("{", run.Json.TrimStart()));             // it took -ojf and wrote the full JSON
        var detected = LiveWhisper.Parse(runner.Runs[0].Json, runner.Runs[0].Said, "auto");
        Assert.Equal("auto", runner.Runs[0].Language);
        Assert.InRange(detected.Detection, 0.000001, 1);                                         // without -np it says how sure it is of the language
        Assert.Matches("^[a-z]{2,3}$", detected.Language);
        Assert.Empty(Directory.GetFiles(Path.Combine(_directory, "real")));
    }

    [Fact]
    public void TheReadingIsTheTextTheLanguageHowSureTheProgramWasOfItAndOfItsWords()
    {
        var reading = LiveWhisper.Parse(Json("hu", "Jó napot.", 0.5, 0.5), "whisper_full_with_state: auto-detected language: hu (p = 0.841000)", "auto");
        Assert.Equal(("hu", 0.841, "Jó napot."), (reading.Language, reading.Detection, reading.Text));
        Assert.Equal(Math.Log(0.5), reading.Confidence, 6);                                  // the program's own markers ([_BEG_]) do not count
        Assert.Equal(0, LiveWhisper.Parse(Json("hu", "Jó napot.", 0.5), "", "auto").Detection);   // it did not say: it is not taken as sure
        Assert.Equal(1, LiveWhisper.Parse(Json("hu", "Jó napot.", 0.5), "", "hu").Detection);     // it was told the language
        var empty = LiveWhisper.Parse("""{"result":{"language":"en"},"transcription":[]}""", "", "en");
        Assert.Equal(("", SpeechReading.NoConfidence), (empty.Text, empty.Confidence));
        Assert.Equal("de", LiveWhisper.Parse("""{"transcription":[]}""", "", "de").Language);
    }
}
