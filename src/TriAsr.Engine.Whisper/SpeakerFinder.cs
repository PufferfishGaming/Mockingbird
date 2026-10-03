using System.Globalization;
using System.Text.RegularExpressions;
using TriAsr.Application;
using TriAsr.Domain;

namespace TriAsr.Engine.Whisper;

/// <summary>
/// Finds who speaks when (ADR: speakers), with sherpa-onnx's speaker diarization program: pyannote segmentation 3.0 finds the turns and TitaNet small tells the voices
/// apart. It runs on the processor, at about a thirtieth of the recording's length with eight threads. What it finds is tidied by <see cref="SpeakerTurns.Tidy"/>.
/// </summary>
public static partial class SpeakerFinder
{
    /// <summary>
    /// How readily two stretches are taken to be one voice (cosine distance). Measured on five AMI meetings: 1.0 merged two people who say little into the others,
    /// 1.05 and 1.1 found every speaker once small "speakers" were merged, 1.15 merged real speakers, and from 1.2 one meeting became a single speaker.
    /// </summary>
    public const string Threshold = "1.05";

    /// <param name="progress">How far it has come, 0 to 1.</param>
    public static async Task<IReadOnlyList<SpeakerTurn>> FindAsync(IProcessRunner runner, string tool, string segmentationModel, string embeddingModel, string audio,
        int threads, CancellationToken token, Action<double>? progress = null)
    {
        var count = threads.ToString(CultureInfo.InvariantCulture);
        var result = await runner.RunAsync(new(tool,
            [$"--segmentation.pyannote-model={segmentationModel}", $"--embedding.model={embeddingModel}", $"--segmentation.num-threads={count}", $"--embedding.num-threads={count}",
             $"--clustering.cluster-threshold={Threshold}", audio],
            Path.GetDirectoryName(tool)!, TimeSpan.FromHours(6), line =>
            {
                var match = Progress().Match(line);
                if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)) progress?.Invoke(Math.Clamp(percent / 100, 0, 1));
            }), token);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("Speaker detection failed. " + (result.StandardError.Length > 300 ? result.StandardError[^300..] : result.StandardError).Trim());
        return Parse(result.StandardOutput + "\n" + result.StandardError);
    }

    /// <summary>Reads the program's output: a line "start -- end speaker_NN" for each turn, in seconds. It must also say that it finished.</summary>
    public static IReadOnlyList<SpeakerTurn> Parse(string output)
    {
        if (!Finished().IsMatch(output)) throw new InvalidDataException("The speaker program did not report a result.");
        var turns = Turn().Matches(output).Select(match => new SpeakerTurn(Milliseconds(match.Groups[1].Value), Milliseconds(match.Groups[2].Value), match.Groups[3].Value)).ToList();
        if (turns.Any(turn => turn.EndMs < turn.StartMs)) throw new InvalidDataException("The speaker program listed a turn that ends before it starts.");
        return turns;
    }

    private static long Milliseconds(string seconds) => (long)Math.Round(double.Parse(seconds, CultureInfo.InvariantCulture) * 1000);
    [GeneratedRegex(@"(?m)^\s*([0-9]+(?:\.[0-9]+)?)\s+--\s+([0-9]+(?:\.[0-9]+)?)\s+(speaker_\d+)\s*$")] private static partial Regex Turn();
    [GeneratedRegex(@"Elapsed seconds:\s*[0-9.]+")] private static partial Regex Finished();
    [GeneratedRegex(@"^progress\s+([0-9.]+)%")] private static partial Regex Progress();
}
