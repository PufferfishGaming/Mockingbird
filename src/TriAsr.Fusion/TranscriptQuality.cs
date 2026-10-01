using System.Text;
using TriAsr.Domain;

namespace TriAsr.Fusion;

public static class TranscriptQuality
{
    public const string RepetitionWarning = "Possible recognition loop: repeated text. Listen to this section; genuine repetitions are preserved.";

    public static FinalTranscript FlagRepetition(FinalTranscript transcript)
    {
        var regions = transcript.Regions.ToArray();
        var normalized = regions.Select(region => Normalize(region.FinalText)).ToArray();
        var flagged = new HashSet<int>();
        for (var start = 0; start < regions.Length; start++)
        {
        if (flagged.Contains(start)) continue;
        for (var period = 1; period <= 4 && start + period * 6 <= regions.Length; period++)
        {
            if (normalized.Skip(start).Take(period).Any(text => text.Length == 0)) continue;
            var words = normalized.Skip(start).Take(period).SelectMany(text => text.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
            if (words.Length < 4 || words.Distinct().Count() < 3) continue;
            var copies = 1;
            while (start + (copies + 1) * period <= regions.Length)
            {
                var next = start + copies * period;
                var matches = true;
                for (var offset = 0; offset < period; offset++)
                {
                    var index = next + offset;
                    if (normalized[index] != normalized[start + offset] ||
                        regions[index].StartMs - regions[index - 1].EndMs > 5000) { matches = false; break; }
                }
                if (!matches) break;
                copies++;
            }
            if (copies < 6 || regions[start + copies * period - 1].EndMs - regions[start].StartMs < 10000) continue;
            for (var index = start; index < start + copies * period; index++) flagged.Add(index);
            break;
        }
        }
        foreach (var index in flagged)
            regions[index] = regions[index] with { Warnings = (regions[index].Warnings ?? []).Append(RepetitionWarning).Distinct().ToArray() };
        return transcript with { Regions = regions };
    }

    private static string Normalize(string text)
    {
        var normalized = new StringBuilder();
        foreach (var character in text)
            normalized.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        return string.Join(' ', normalized.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
