using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using TriAsr.Application;
using TriAsr.Domain;

namespace TriAsr.Engine.Whisper;

public sealed partial class WhisperEngine(IProcessRunner runner, string executable, string model, int threads)
{
    public sealed record LanguageDetection(string Language, double Confidence, IReadOnlyList<string> WindowLanguages);
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
        // unchanged, and it runs faster. See ADR-0007.
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
        if (result.ExitCode != 0) throw new InvalidOperationException($"Whisper exited with code {result.ExitCode}; see its saved runtime output.");
        var actual = IdentifyBackend(result.StandardError);
        if (actual != backend) throw new InvalidOperationException($"Whisper requested {backend}, actually used {actual}.");
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
            if (start < 0 || end < start) throw new InvalidDataException("Invalid Whisper segment timestamps.");
            segments.Add(new(start, end, item.GetProperty("text").GetString()?.Trim() ?? ""));
        }
        return segments;
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
    [GeneratedRegex(@"whisper_print_progress_callback:.*?progress\s*=\s*([0-9.]+)%")] private static partial Regex ProgressResult();
}
