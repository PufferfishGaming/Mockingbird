using System.Globalization;
using System.Text.RegularExpressions;
using TriAsr.Application;
using TriAsr.Domain;

namespace TriAsr.Engine.Whisper;

/// <summary>Finds where speech is in the 16 kHz recording, using the Silero detector that ships with whisper.cpp.</summary>
public static partial class VadSegmenter
{
    public static async Task<IReadOnlyList<SpeechSpan>> DetectAsync(IProcessRunner runner, string tool, string model, string audio, int threads, CancellationToken token)
    {
        var result = await runner.RunAsync(new(tool, ["-f", audio, "-vm", model, "-t", threads.ToString(CultureInfo.InvariantCulture), "-np"],
            Path.GetDirectoryName(tool)!, TimeSpan.FromMinutes(10)), token);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("Speech detection failed. " + (result.StandardError.Length > 300 ? result.StandardError[^300..] : result.StandardError).Trim());
        return Parse(result.StandardOutput);
    }

    /// <summary>
    /// Reads the detector's text output. The tool prints times in hundredths of a second ("start = 227.00" is 2.27 s).
    /// The announced segment count must match, so a changed output format fails loudly instead of yielding a wrong plan.
    /// </summary>
    public static IReadOnlyList<SpeechSpan> Parse(string output)
    {
        var header = Header().Match(output);
        if (!header.Success) throw new InvalidDataException("The speech detector did not report a result.");
        var spans = Segment().Matches(output)
            .Select(match => new SpeechSpan(Milliseconds(match.Groups[1].Value), Milliseconds(match.Groups[2].Value))).ToList();
        if (spans.Count != int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture))
            throw new InvalidDataException($"The speech detector announced {header.Groups[1].Value} segments but listed {spans.Count}.");
        if (spans.Any(span => span.EndMs < span.StartMs)) throw new InvalidDataException("The speech detector listed a segment that ends before it starts.");
        return spans;
    }

    private static long Milliseconds(string hundredths) => (long)Math.Round(double.Parse(hundredths, CultureInfo.InvariantCulture) * 10);
    [GeneratedRegex(@"Detected\s+(\d+)\s+speech segments")] private static partial Regex Header();
    [GeneratedRegex(@"Speech segment\s+\d+:\s*start\s*=\s*([0-9.]+),\s*end\s*=\s*([0-9.]+)")] private static partial Regex Segment();
}
