using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using TriAsr.Application;
using TriAsr.Domain;

namespace TriAsr.Engine.Whisper;

public sealed partial class WhisperEngine(IProcessRunner runner, string executable, string model, int threads)
{
    /// <param name="Language">The language the recording is read in; two joined with <c>+</c> when it was found to switch between the two languages chosen.</param>
    /// <param name="Blocks">For a recording in two languages, the stretches that are in each; null otherwise.</param>
    public sealed record LanguageDetection(string Language, double Confidence, IReadOnlyList<string> WindowLanguages, IReadOnlyList<LanguageBlock>? Blocks = null);

    /// <summary>The most files given to one run of the program, so that its command line stays well inside what Windows allows.</summary>
    private const int FilesPerRun = 50;

    /// <summary>
    /// Detects the language of each file, without reading the words: one run of the program for up to <see cref="FilesPerRun"/> files, so the model is loaded once.
    /// </summary>
    public async Task<IReadOnlyList<(string Language, double Detection)>> DetectEachAsync(IReadOnlyList<string> files, string backend, CancellationToken token, Action<double>? progress = null)
    {
        var found = new Dictionary<string, (string, double)>(StringComparer.OrdinalIgnoreCase);
        for (var first = 0; first < files.Count; first += FilesPerRun)
        {
            var batch = files.Skip(first).Take(FilesPerRun).ToArray();
            var arguments = new List<string> { "-m", model, "-l", "auto", "-dl", "-t", threads.ToString(CultureInfo.InvariantCulture) };
            if (backend == "cpu") arguments.Add("-ng");
            foreach (var file in batch) arguments.AddRange(["-f", file]);
            var result = await runner.RunAsync(new(executable, arguments, Path.GetDirectoryName(executable)!, TimeSpan.FromMinutes(30)), token);
            if (result.ExitCode != 0) throw new InvalidOperationException("Whisper language detection did not return a valid language and probability.");
            string? current = null;
            foreach (var line in (result.StandardError + "\n" + result.StandardOutput).Split('\n'))
            {
                if (Processing().Match(line) is { Success: true } processing) current = processing.Groups[1].Value;
                else if (current is not null && LanguageResult().Match(line) is { Success: true } match)
                {
                    found[current] = (match.Groups[1].Value, double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
                    current = null;
                }
            }
            progress?.Invoke(Math.Min(1, (first + batch.Length) / (double)files.Count));
        }
        return files.Select(file => found.TryGetValue(file, out var detected) ? detected
            : throw new InvalidOperationException("Whisper language detection did not return a valid language and probability.")).ToArray();
    }

    /// <summary>Reads each file in one language, as a phrase of live dictation is read, and says how sure the program was of the words (one run for many files).</summary>
    public async Task<IReadOnlyList<SpeechReading>> ReadEachAsync(IReadOnlyList<string> files, string language, string directory, string backend, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        var readings = new List<SpeechReading>();
        for (var first = 0; first < files.Count; first += FilesPerRun)
        {
            var batch = files.Skip(first).Take(FilesPerRun).ToArray();
            var outputs = batch.Select((_, i) => Path.Combine(directory, $"{language}-{first + i:D5}")).ToArray();
            var arguments = new List<string> { "-m", model, "-l", language, "-t", threads.ToString(CultureInfo.InvariantCulture), "-mc", "0", "-nt", "-ojf", "-np" };
            if (backend == "cpu") arguments.Add("-ng");
            for (var i = 0; i < batch.Length; i++) arguments.AddRange(["-f", batch[i], "-of", outputs[i]]);
            var result = await runner.RunAsync(new(executable, arguments, Path.GetDirectoryName(executable)!, TimeSpan.FromHours(2)), token);
            if (result.ExitCode != 0) throw new InvalidOperationException($"Whisper stopped with exit code {result.ExitCode}. See the saved engine output.");
            foreach (var output in outputs)
            {
                if (!File.Exists(output + ".json")) throw new InvalidOperationException("Whisper did not write its reading of a stretch of the recording.");
                readings.Add(LiveWhisper.Parse(await File.ReadAllTextAsync(output + ".json", token), "", language));
            }
        }
        return readings;
    }
    /// <param name="sampleStarts">Where to take the 15 s samples from (seconds), chosen where there is speech; without it the start, middle and end are used.</param>
    public async Task<LanguageDetection> DetectLanguageAsync(string audio, double seconds, string directory, string backend, string ffmpeg, CancellationToken token, IReadOnlyList<double>? sampleStarts = null)
    {
        Directory.CreateDirectory(directory);
        var detections = new List<(string Language, double Confidence)>();
        var offsets = sampleStarts is { Count: > 0 } ? sampleStarts.ToArray()
            : seconds <= 30 ? new[] { 0d } : new[] { 0d, Math.Max(0, seconds / 2 - 7.5), Math.Max(0, seconds - 15) };
        for (var i = 0; i < offsets.Length; i++)
        {
            var window = Path.Combine(directory, $"window-{i}.wav");
            if (!File.Exists(window))
            {
                var convert = await runner.RunAsync(new(ffmpeg, ["-nostdin", "-v", "error", "-n", "-threads", "2", "-ss", offsets[i].ToString(CultureInfo.InvariantCulture), "-i", audio, "-t", "15", "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", window], directory, TimeSpan.FromMinutes(1)), token);
                if (convert.ExitCode != 0) throw new InvalidOperationException("Language sample extraction failed.");
            }
            var arguments = new List<string> { "-m", model, "-f", window, "-l", "auto", "-dl", "-t", threads.ToString(CultureInfo.InvariantCulture) };
            if (backend == "cpu") arguments.Add("-ng");
            var result = await runner.RunAsync(new(executable, arguments, Path.GetDirectoryName(executable)!, TimeSpan.FromMinutes(10)), token);
            await File.WriteAllTextAsync(Path.Combine(directory, $"window-{i}.stderr.txt"), result.StandardError, token);
            var match = LanguageResult().Match(result.StandardError + result.StandardOutput);
            if (result.ExitCode != 0 || !match.Success) throw new InvalidOperationException("Whisper language detection did not return a valid language and probability.");
            detections.Add((match.Groups[1].Value, double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)));
        }
        var winner = detections.GroupBy(value => value.Language).OrderByDescending(group => group.Count()).ThenByDescending(group => group.Sum(value => value.Confidence)).First();
        var detection = new LanguageDetection(winner.Key, winner.Average(value => value.Confidence) * winner.Count() / detections.Count, detections.Select(value => value.Language).ToArray());
        await File.WriteAllTextAsync(Path.Combine(directory, "language.json"), JsonSerializer.Serialize(detection), token);
        return detection;
    }
    /// <param name="vadModel">When given, Whisper skips the stretches without speech itself (timestamps stay on the original timeline).</param>
    public async Task<EngineTranscript> TranscribeAsync(string audio, double audioSeconds, string language, string directory, string backend, CancellationToken token, Action<double>? progress = null, string? vadModel = null)
    {
        if (backend is not ("cpu" or "vulkan" or "cuda" or "rocm")) throw new ArgumentException("Unsupported backend.");
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "raw");
        // "-mc 0": each 30 s window is decoded without the previous text as context. With the context, one repeated phrase feeds itself
        // (a song scored 173% word error rate, a video 830 copies of one phrase); without it the same song scores 5.2%, speech text is
        // unchanged, and it runs faster.
        var arguments = new List<string> { "-m", model, "-f", audio, "-l", language, "-t", threads.ToString(CultureInfo.InvariantCulture), "-mc", "0", "-ojf", "-otxt", "-of", output };
        if (vadModel is not null) arguments.AddRange(["--vad", "-vm", vadModel, .. VadSegmenter.Thresholds]);
        if (backend == "cpu") arguments.Add("-ng");
        if (progress is not null) arguments.Add("-pp");
        var result = await runner.RunAsync(new(executable, arguments, Path.GetDirectoryName(executable)!, TimeSpan.FromHours(12), line =>
        {
            var match = ProgressResult().Match(line);
            if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)) progress?.Invoke(percent / 100);
        }), token);
        await File.WriteAllTextAsync(Path.Combine(directory, "runtime.stderr.txt"), result.StandardError, token);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Whisper stopped with exit code {result.ExitCode}. See the saved engine output.");
        var actual = IdentifyBackend(result.StandardError);
        if (actual != backend) throw new InvalidOperationException($"Whisper was set to use {backend}, but used {actual}.");
        var raw = await File.ReadAllTextAsync(output + ".json", token);
        using var document = JsonDocument.Parse(raw);
        var segments = ParseSegments(document.RootElement);
        var device = actual == "cpu" ? "CPU" : actual == "vulkan" ? VulkanDevice().Match(result.StandardError).Groups[1].Value.Trim() : actual.ToUpperInvariant() + " GPU";
        var version = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(executable, token)))[..16];
        var timing = LoadTime().Match(result.StandardError);
        double? loadSeconds = timing.Success ? double.Parse(timing.Groups[1].Value, CultureInfo.InvariantCulture) / 1000 : null;
        return new("Whisper", Path.GetFileName(model), $"whisper.cpp binary {version}", backend, actual, device,
            document.RootElement.GetProperty("result").GetProperty("language").GetString()!, audioSeconds, result.Seconds,
            segments, string.Join(" ", segments.Select(segment => segment.Text)), true, loadSeconds, result.PeakRamBytes, result.CpuSeconds);
    }
    public static IReadOnlyList<TranscriptSegment> ParseSegments(JsonElement root)
    {
        var segments = new List<TranscriptSegment>();
        foreach (var item in root.GetProperty("transcription").EnumerateArray())
        {
            var offsets = item.GetProperty("offsets");
            var start = offsets.GetProperty("from").GetInt64(); var end = offsets.GetProperty("to").GetInt64();
            if (start < 0 || end < start) throw new InvalidDataException("Whisper returned invalid timestamps.");
            segments.Add(new(start, end, item.GetProperty("text").GetString()?.Trim() ?? ""));
        }
        return segments;
    }
    /// <summary>
    /// The words of Whisper's full output (<c>-ojf</c>) with their times, moved by <paramref name="offsetMs"/>: a word begins at a token that starts with a space
    /// (or the first token of a segment), and the program's own markers ([_BEG_] and the like) are left out.
    /// </summary>
    public static IReadOnlyList<TimedWord> ParseWords(JsonElement root, long offsetMs = 0)
    {
        var words = new List<TimedWord>();
        foreach (var segment in root.GetProperty("transcription").EnumerateArray())
        {
            if (!segment.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Array) continue;
            var first = true;
            foreach (var token in tokens.EnumerateArray())
            {
                var text = token.TryGetProperty("text", out var spelled) ? spelled.GetString() ?? "" : "";
                if (text.StartsWith("[_", StringComparison.Ordinal) || text.Length == 0 || !token.TryGetProperty("offsets", out var offsets)) continue;
                var start = offsets.GetProperty("from").GetInt64() + offsetMs;
                var end = offsets.GetProperty("to").GetInt64() + offsetMs;
                if (first || text.StartsWith(' ')) words.Add(new(start, Math.Max(start, end), text));
                else words[^1] = words[^1] with { EndMs = Math.Max(words[^1].EndMs, end), Text = words[^1].Text + text };
                first = false;
            }
        }
        return words;
    }
    public static string IdentifyBackend(string log)
    {
        if (log.Contains("using Vulkan", StringComparison.OrdinalIgnoreCase)) return "vulkan";
        if (log.Contains("using HIP", StringComparison.OrdinalIgnoreCase)) return "rocm";
        if (log.Contains("using CUDA", StringComparison.OrdinalIgnoreCase))
            return log.Contains("ROCm", StringComparison.OrdinalIgnoreCase) || log.Contains("HIP devices", StringComparison.OrdinalIgnoreCase) ? "rocm" : "cuda";
        return "cpu";
    }
    [GeneratedRegex(@"ggml_vulkan: \d+ = (.+?) \(")] private static partial Regex VulkanDevice();
    [GeneratedRegex(@"(?:auto-detected|detected) language:\s*([a-z]+)\s*\(p\s*=\s*([0-9.]+)\)")] private static partial Regex LanguageResult();
    [GeneratedRegex(@"load time\s*=\s*([0-9.]+)\s*ms")] private static partial Regex LoadTime();
    [GeneratedRegex(@"processing '(.+?)' \(")] private static partial Regex Processing();
    [GeneratedRegex(@"whisper_print_progress_callback:.*?progress\s*=\s*([0-9.]+)%")] private static partial Regex ProgressResult();
}
