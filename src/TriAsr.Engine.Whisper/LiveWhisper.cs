using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TriAsr.Application;

namespace TriAsr.Engine.Whisper;

/// <summary>
/// Reads one phrase of live dictation with the same <c>whisper-cli</c> that transcribes whole recordings, started once per phrase. With the smaller
/// model on a graphics card that takes a second or two for a phrase of a few seconds, which is fast enough to type it while the next one is spoken.
/// With two languages the phrase may be read twice (<see cref="LiveLanguagePicker"/>).
/// </summary>
public sealed partial class LiveWhisper(IProcessRunner runner, string executable, string model, int threads, string backend, string folder) : ILiveRecognizer
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    [GeneratedRegex(@"(?:auto-detected|detected) language:\s*([a-z]+)\s*\(p\s*=\s*([0-9.]+)\)")]
    private static partial Regex DetectedLanguage();

    public async Task<LivePhrase> RecognizeAsync(byte[] wav, string language, string? recent, CancellationToken token)
    {
        if (!File.Exists(model)) throw new LiveException(LiveMessages.NoModel);
        if (wav.Length > 60L * 32_000 + 44) throw new LiveException(LiveMessages.TooLong);       // a minute of sound: far more than a phrase
        Directory.CreateDirectory(folder);
        var audio = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            await File.WriteAllBytesAsync(audio, wav, token).ConfigureAwait(false);
            return await LiveLanguagePicker.PickAsync((code, cancel) => ReadAsync(audio, code, cancel), language, recent, token).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(audio); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Reads the phrase in one language, or in the one the program detects (<c>auto</c>).</summary>
    private async Task<SpeechReading> ReadAsync(string audio, string language, CancellationToken token)
    {
        var name = Path.Combine(folder, Guid.NewGuid().ToString("N"));
        var arguments = new List<string>
        {
            "-m", model, "-f", audio, "-l", language, "-t", threads.ToString(CultureInfo.InvariantCulture), "-mc", "0", "-nt", "-ojf", "-of", name
        };
        if (language != "auto") arguments.Add("-np");                     // when it detects the language it has to say which, and how sure it is
        if (backend == "cpu") arguments.Add("-ng");
        try
        {
            var result = await runner.RunAsync(new(executable, arguments, Path.GetDirectoryName(executable)!, Timeout), token).ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(name + ".json")) throw new LiveException(LiveMessages.Failed);
            return Parse(await File.ReadAllTextAsync(name + ".json", token).ConfigureAwait(false), result.StandardError, language);
        }
        finally
        {
            try { File.Delete(name + ".json"); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Reads what <c>whisper-cli</c> wrote (<c>-ojf</c>) and what it said about the language while it ran.</summary>
    /// <param name="requested">The language it was told, or <c>auto</c>.</param>
    public static SpeechReading Parse(string json, string standardError, string requested)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var language = root.TryGetProperty("result", out var result) && result.TryGetProperty("language", out var found) && found.GetString() is { Length: > 0 } code ? code : requested;
            var words = new List<string>();
            double sum = 0; var count = 0;
            if (root.TryGetProperty("transcription", out var transcription) && transcription.ValueKind == JsonValueKind.Array)
                foreach (var item in transcription.EnumerateArray())
                {
                    if (item.TryGetProperty("text", out var piece) && piece.GetString() is { } value) words.Add(value);
                    if (!item.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Array) continue;
                    foreach (var token in tokens.EnumerateArray())
                    {
                        var spelled = token.TryGetProperty("text", out var spelling) ? spelling.GetString() ?? "" : "";
                        if (spelled.StartsWith("[_", StringComparison.Ordinal) || spelled.Trim().Length == 0 || !token.TryGetProperty("p", out var probability)) continue;
                        sum += Math.Log(Math.Max(probability.GetDouble(), 1e-6));
                        count++;
                    }
                }
            var text = string.Join(" ", words).Trim();
            var detection = 1d;
            if (requested == "auto")
                detection = DetectedLanguage().Match(standardError) is { Success: true } match && double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 0;
            return new SpeechReading(language, detection, count == 0 || text.Length == 0 ? SpeechReading.NoConfidence : sum / count, text);
        }
        catch (JsonException error) { throw new LiveException(LiveMessages.Failed, error); }
    }
}
