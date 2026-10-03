using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using TriAsr.App;
using TriAsr.Application;
using TriAsr.Audio.Live;
using TriAsr.Domain;
using static TriAsr.Ui.SmokeTests.Sound;

namespace TriAsr.Ui.SmokeTests;

/// <summary>
/// What the web page does with the microphone (<c>live.js</c>) against what the programs do: the same sound is cut into phrases at the same places, a phrase becomes the same WAV file,
/// words are added to a note the same way. The page's code is run by Node; the tests are skipped on a computer without it.
/// </summary>
public sealed class WebLiveTests
{
    private sealed class NodeFactAttribute : FactAttribute
    {
        public NodeFactAttribute() { if (Node() is null) Skip = "Node.js was not found: the web page's microphone code is checked on a computer that has it."; }
    }

    private sealed class NodeTheoryAttribute : TheoryAttribute
    {
        public NodeTheoryAttribute() { if (Node() is null) Skip = "Node.js was not found: the web page's microphone code is checked on a computer that has it."; }
    }

    private static string? _node;
    private static bool _looked;

    private static string? Node()
    {
        if (_looked) return _node;
        _looked = true;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("node", "--version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            process!.WaitForExit(10_000);
            if (process.ExitCode == 0) _node = "node";
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return _node;
    }

    private static string LiveScript => Path.Combine(TranslationSources.AppFolder, "Web", "live.js");
    private static string Harness => Path.Combine(TranslationSources.RepositoryRoot(), "tests", "TriAsr.Ui.SmokeTests", "WebLive.js");

    private static JsonElement Run(object request)
    {
        var json = JsonSerializer.Serialize(request);
        var start = new ProcessStartInfo("node", $"\"{Harness}\"") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = System.Text.Encoding.UTF8 };
        using var process = Process.Start(start)!;
        process.StandardInput.Write(json);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(60_000), "Node did not finish.");
        Assert.True(process.ExitCode == 0, "Node failed: " + error.Result);
        return JsonDocument.Parse(output.Result).RootElement.Clone();
    }

    private static string Temporary(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N") + ".pcm");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Noise(double seconds, double rms, int seed)
    {
        var random = new Random(seed);
        var bytes = new byte[(int)(seconds * 16_000) * 2];
        var amplitude = rms * Math.Sqrt(3) * 32768;
        for (var i = 0; i < bytes.Length / 2; i++) System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)((random.NextDouble() * 2 - 1) * amplitude));
        return bytes;
    }

    public static IEnumerable<object[]> Scenarios()
    {
        var sounds = new Dictionary<string, (byte[] Sound, bool Flush)>
        {
            ["two phrases"] = (Join(Quiet(0.5), Tone(1.2, 0.08), Quiet(1.2), Tone(2, 0.05), Quiet(1.5)), false),
            ["a noisy room, a cough and a sentence"] = (Join(Noise(1.0, 0.01, 3), Tone(1.5, 0.1), Noise(1.5, 0.01, 4), Tone(0.2, 0.1), Noise(1.5, 0.01, 5), Tone(3, 0.07), Noise(1.0, 0.01, 6)), false),
            ["speech that goes on for longer than a phrase may"] = (Join(Quiet(0.5), Tone(12, 0.08), Quiet(0.4), Tone(15, 0.08), Quiet(0.3), Tone(10, 0.08), Quiet(2)), false),
            ["speech from the first moment, then a pause and more speech"] = (Join(Tone(1, 0.08), Quiet(1.5), Tone(1.5, 0.08), Quiet(1.5)), false),
            ["the recording stops in the middle of a phrase"] = (Join(Quiet(0.5), Tone(1.5, 0.08)), true),
            ["nothing but a quiet room"] = (Quiet(5), true)
        };
        foreach (var (name, scenario) in sounds)
            foreach (var chunk in new[] { 1600, 997 })
                yield return [name, scenario.Sound, scenario.Flush, chunk];
    }

    [NodeTheory]
    [MemberData(nameof(Scenarios))]
    public void TheBrowserCutsTheSoundIntoTheSamePhrasesAsTheProgram(string name, byte[] sound, bool flush, int chunk)
    {
        var path = Temporary(sound);
        try
        {
            var expected = new List<(int Duration, int Speech, string Sha)>();
            var detector = new UtteranceDetector(phrase => expected.Add(((int)phrase.Duration.TotalMilliseconds, (int)phrase.Speech.TotalMilliseconds, Convert.ToHexString(SHA256.HashData(phrase.Pcm)).ToLowerInvariant())));
            for (var at = 0; at < sound.Length; at += chunk * 2) detector.Feed(sound.AsSpan(at, Math.Min(chunk * 2, sound.Length - at)));
            if (flush) detector.Flush();

            var found = Run(new { op = "detect", live = LiveScript, file = path, chunk, flush }).EnumerateArray()
                .Select(item => (item.GetProperty("durationMs").GetInt32(), item.GetProperty("speechMs").GetInt32(), item.GetProperty("sha").GetString()!)).ToList();
            Assert.Equal(expected, found);
            if (name != "nothing but a quiet room") Assert.NotEmpty(expected);                   // the comparison is not of two empty lists
        }
        finally { File.Delete(path); }
    }

    [NodeFact]
    public void TheBrowserDetectsWithTheSameLimitsAsTheProgram()
    {
        var js = Run(new { op = "defaults", live = LiveScript });
        var options = new UtteranceOptions();
        Assert.Equal(options.StartLevel, js.GetProperty("startLevel").GetDouble());
        Assert.Equal(options.NoiseMultiplier, js.GetProperty("noiseMultiplier").GetDouble());
        Assert.Equal(options.StartMs, js.GetProperty("startMs").GetInt32());
        Assert.Equal(options.PreRollMs, js.GetProperty("preRollMs").GetInt32());
        Assert.Equal(options.EndSilenceMs, js.GetProperty("endSilenceMs").GetInt32());
        Assert.Equal(options.TailMs, js.GetProperty("tailMs").GetInt32());
        Assert.Equal(options.MinSpeechMs, js.GetProperty("minSpeechMs").GetInt32());
        Assert.Equal(options.MaxMs, js.GetProperty("maxMs").GetInt32());
    }

    [NodeFact]
    public void APhraseBecomesTheSameWavFileInTheBrowserAsInTheProgram()
    {
        var pcm = Join(Tone(0.7, 0.05), Quiet(0.2));
        var path = Temporary(pcm);
        try
        {
            var js = Run(new { op = "wav", live = LiveScript, file = path });
            var wav = PhraseText.Wav(pcm);
            Assert.Equal(wav.Length, js.GetProperty("length").GetInt32());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(wav)).ToLowerInvariant(), js.GetProperty("sha").GetString());
        }
        finally { File.Delete(path); }
    }

    [NodeFact]
    public void WordsAreAddedToANoteTheSameWayInTheBrowserAsInTheProgram()
    {
        string[][] cases =
        [
            ["", "Hello."], ["Hello.", "World."], ["Hello.\n", "World."], ["Hello. ", "World."], ["今日は。", "元気です。"], ["Hello.", "元気です。"], ["Hello.", ""], ["안녕하세요", "감사합니다"], ["Mixed 日本語", "text"]
        ];
        var js = Run(new { op = "append", live = LiveScript, cases }).EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal(cases.Select(item => PhraseText.Append(item[0], item[1])), js);
    }

    [NodeFact]
    public void TheLanguagesOfARecordingAreReadAndWrittenTheSameWayInTheBrowserAsInTheProgram()
    {
        string?[] saved = [null, "", "auto", "en", "en+hu", " HU + en ", "en hu", "en+en", "auto+en", "en+hu+de", "klingon", "en+klingon"];
        string[][] chosen = [["auto", "hu"], ["en", ""], ["en", "hu"], ["en", "en"], ["", "hu"], ["de", "auto"]];
        var known = LanguageCatalog.All.Select(language => language.Code).ToArray();
        var js = Run(new { op = "languages", live = LiveScript, saved, chosen, known });
        Assert.Equal(saved.Select(value => SpeechLanguages.Split(value)), js.GetProperty("split").EnumerateArray().Select(item => (item.GetProperty("first").GetString()!, item.GetProperty("second").GetString()!)));
        Assert.Equal(chosen.Select(pair => SpeechLanguages.Join(pair[0], pair[1])), js.GetProperty("joined").EnumerateArray().Select(item => item.GetString()));
    }

    [NodeTheory]
    [InlineData(48_000, 1024)] [InlineData(44_100, 1024)] [InlineData(16_000, 1024)] [InlineData(22_050, 777)] [InlineData(96_000, 1024)]
    public void TheMicrophonesRateIsBroughtDownTo16kHzWithoutChangingTheSound(int rate, int chunk)
    {
        var js = Run(new { op = "resample", live = LiveScript, rate, hz = 440, seconds = 2, chunk });
        Assert.InRange(js.GetProperty("count").GetInt32(), 32_000 - 2, 32_000 + 1);           // two seconds at 16 kHz, to the sample
        Assert.InRange(js.GetProperty("rms").GetDouble(), 0.5 / Math.Sqrt(2) * 0.95, 0.5 / Math.Sqrt(2) * 1.02);     // a sine of 0.5 has an RMS of 0.354, and averaging dulls it only a little
        Assert.InRange(js.GetProperty("peak").GetDouble(), 0.45, 0.51);
    }

    [NodeFact]
    public void TheKeysOfThePageAreNamedAndJudgedAsThePagePromises()
    {
        object[] events =
        [
            new { code = "KeyN", ctrlKey = false, altKey = true, shiftKey = false },         // Alt+N
            new { code = "KeyK", ctrlKey = true, altKey = false, shiftKey = true },          // Ctrl+Shift+K
            new { code = "F9", ctrlKey = false, altKey = false, shiftKey = false },          // F9 alone
            new { code = "Space", ctrlKey = false, altKey = true, shiftKey = false },        // Alt+Space: the system menu
            new { code = "KeyA", ctrlKey = false, altKey = false, shiftKey = false },        // A alone
            new { code = "KeyC", ctrlKey = true, altKey = false, shiftKey = false },         // Ctrl+C
            new { code = "ControlLeft", ctrlKey = true, altKey = false, shiftKey = false },  // only a modifier
            new { code = "Digit5", ctrlKey = true, altKey = true, shiftKey = false },        // Ctrl+Alt+5
            new { code = "ArrowLeft", ctrlKey = false, altKey = true, shiftKey = false }     // Alt+Left: back
        ];
        var js = Run(new { op = "keys", live = LiveScript, events }).EnumerateArray().ToArray();
        string?[] labels = ["Alt+N", "Ctrl+Shift+K", "F9", "Alt+Space", "A", "Ctrl+C", "", "Ctrl+Alt+5", "Alt+Left"];
        string?[] problems = [null, null, null, "common", "needsModifier", "needsModifier", "none", null, "common"];
        for (var i = 0; i < events.Length; i++)
        {
            Assert.Equal(labels[i], js[i].GetProperty("label").GetString());
            var problem = js[i].GetProperty("problem");
            Assert.Equal(problems[i], problem.ValueKind == JsonValueKind.Null ? null : problem.GetString());
            if (labels[i]!.Length > 0) Assert.Equal(js[i].GetProperty("combo").GetProperty("code").GetString(), js[i].GetProperty("again").GetProperty("code").GetString());     // what is saved is read back as the same keys
        }
    }
}
