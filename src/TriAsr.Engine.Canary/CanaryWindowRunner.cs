using System.Text.Json;
using TriAsr.Application;
using TriAsr.Domain;

namespace TriAsr.Engine.Canary;

/// <summary>What Canary returned for one window of the chunk plan.</summary>
public sealed record WindowResult(int Index, long StartMs, long EndMs, bool OverlapsPrevious, string Text, string Raw);

/// <summary>
/// Runs the recogniser over the planned windows and saves every finished window, so a run that is stopped or crashes continues
/// from the next window instead of from the start. Kept free of native code so it can be tested without the model.
/// </summary>
public static class CanaryWindowRunner
{
    private sealed record Saved(string Key, WindowResult Result);

    /// <param name="key">Identifies the model and language; a saved window made with another key is ignored.</param>
    /// <param name="readPieces">The audio of a window, in pieces the recogniser can take.</param>
    public static IReadOnlyList<WindowResult> Run(IReadOnlyList<CanaryWindow> windows, string? checkpointDirectory, string key,
        Func<CanaryWindow, IEnumerable<float[]>> readPieces, Func<float[], (string Full, string Raw)> recognize, Action<double>? progress = null)
    {
        if (checkpointDirectory is not null) Directory.CreateDirectory(checkpointDirectory);
        var results = new List<WindowResult>(windows.Count);
        double total = Math.Max(1, windows.Sum(window => window.DurationMs)), done = 0;
        foreach (var window in windows)
        {
            var file = checkpointDirectory is null ? null : Path.Combine(checkpointDirectory, $"{window.Index:D6}.json");
            var result = file is not null ? Load(file, window, key) : null;
            if (result is null)
            {
                var texts = new List<string>(); var raws = new List<string>();
                foreach (var samples in readPieces(window))
                {
                    var (full, raw) = recognize(samples);
                    if (full.Trim().Length > 0) texts.Add(full.Trim());
                    raws.Add(raw);
                }
                result = new(window.Index, window.StartMs, window.EndMs, window.OverlapsPrevious, string.Join(" ", texts), string.Join(" ", raws));
                if (file is not null) Save(file, key, result);
            }
            results.Add(result);
            done += window.DurationMs;
            progress?.Invoke(Math.Clamp(done / total, 0, 1));
        }
        return results;
    }

    /// <summary>The text of the whole recording (repeats at overlaps removed) and one timed segment per window that has text.</summary>
    public static (string Text, IReadOnlyList<TranscriptSegment> Segments) Assemble(IReadOnlyList<WindowResult> results)
    {
        var text = OverlapText.Join(results.Select(result => (result.Text, result.OverlapsPrevious)));
        var segments = new List<TranscriptSegment>(); var previous = "";
        foreach (var result in results)
        {
            var kept = result.OverlapsPrevious && previous.Length > 0 ? OverlapText.TrimRepeatedStart(previous, result.Text) : result.Text;
            if (kept.Length > 0) segments.Add(new(result.StartMs, result.EndMs, kept));
            if (result.Text.Length > 0) previous = result.Text;
        }
        return (text, segments);
    }

    private static WindowResult? Load(string file, CanaryWindow window, string key)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(file));
            return saved is not null && saved.Key == key && saved.Result.Index == window.Index && saved.Result.StartMs == window.StartMs
                && saved.Result.EndMs == window.EndMs && saved.Result.OverlapsPrevious == window.OverlapsPrevious ? saved.Result : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static void Save(string file, string key, WindowResult result)
    {
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Saved(key, result)));
        File.Move(temporary, file, true);
    }
}
