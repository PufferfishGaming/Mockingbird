using System.Globalization;
using TriAsr.Domain;

namespace TriAsr.Application;

/// <summary>
/// Telling the speakers of a recording apart (ADR: speakers). The speaker program (pyannote segmentation with TitaNet small embeddings, through sherpa-onnx) finds
/// the turns; these rules make them usable in a transcript. Measured on five AMI meetings (four speakers each, 2 hours): the program's own clustering split four
/// people into 11 to 27 "speakers"; merging every "speaker" with less than <see cref="SmallestShare"/> of the speech into the speakers around it found exactly four
/// in all five, with 95 % of the words given to the right speaker. Told how many speakers there are, keeping that many (the ones who talk most) did as well,
/// where the program's own way of using the number got 80 %.
/// </summary>
public static class SpeakerTurns
{
    /// <summary>A "speaker" with less of the speech than this is taken to be a mistake of the clustering and merged into the speakers around it.</summary>
    public const double SmallestShare = 0.04;

    /// <summary>
    /// Merges the speakers that are not kept into the speakers around them: each of their turns goes to the kept speaker whose turn is nearest in time.
    /// Kept are the <paramref name="count"/> speakers who talk most, or, without a count, every speaker with at least <see cref="SmallestShare"/> of the speech.
    /// The speakers are then numbered "1", "2"... in the order they first speak, and turns of one speaker that follow each other are joined.
    /// </summary>
    public static IReadOnlyList<SpeakerTurn> Tidy(IReadOnlyList<SpeakerTurn> turns, int count = 0)
    {
        var ordered = turns.Where(turn => turn.EndMs > turn.StartMs).OrderBy(turn => turn.StartMs).ThenBy(turn => turn.EndMs).ToList();
        if (ordered.Count == 0) return [];
        var talk = ordered.GroupBy(turn => turn.Speaker).ToDictionary(group => group.Key, group => group.Sum(turn => turn.EndMs - turn.StartMs));
        var total = (double)talk.Values.Sum();
        var kept = (count > 0
            ? talk.OrderByDescending(item => item.Value).ThenBy(item => item.Key, StringComparer.Ordinal).Take(count).Select(item => item.Key)
            : talk.Where(item => item.Value >= SmallestShare * total).Select(item => item.Key)).ToHashSet();
        if (kept.Count == 0) kept.Add(talk.MaxBy(item => item.Value).Key);
        var anchors = ordered.Where(turn => kept.Contains(turn.Speaker)).ToList();
        var merged = ordered.Select(turn => kept.Contains(turn.Speaker) ? turn : turn with { Speaker = anchors.MinBy(anchor => Gap(anchor, turn.StartMs, turn.EndMs))!.Speaker }).ToList();

        var numbers = new Dictionary<string, string>();
        var result = new List<SpeakerTurn>();
        foreach (var turn in merged)
        {
            if (!numbers.TryGetValue(turn.Speaker, out var number)) numbers[turn.Speaker] = number = (numbers.Count + 1).ToString(CultureInfo.InvariantCulture);
            if (result.Count > 0 && result[^1].Speaker == number && turn.StartMs <= result[^1].EndMs) result[^1] = result[^1] with { EndMs = Math.Max(result[^1].EndMs, turn.EndMs) };
            else result.Add(turn with { Speaker = number });
        }
        return result;
    }

    /// <summary>The speaker talking at a moment; when two talk at once, the one whose turn began last; when nobody talks, the one whose turn is nearest.</summary>
    public static string? At(IReadOnlyList<SpeakerTurn> turns, long moment)
    {
        if (turns.Count == 0) return null;
        var here = turns.Where(turn => turn.StartMs <= moment && moment < turn.EndMs).MaxBy(turn => turn.StartMs);
        return (here ?? turns.MinBy(turn => Gap(turn, moment, moment)))!.Speaker;
    }

    /// <summary>
    /// Gives every segment its speaker, and cuts a segment where the speaker changes inside it (measured: a third of Whisper's segments in a meeting hold more than one
    /// speaker). A word belongs to the speaker talking at its middle. A segment is cut only when its words, put back together, are its text; otherwise it goes whole
    /// to the speaker who talks most during it.
    /// </summary>
    /// <param name="words">Whisper's words with their times, for the whole recording.</param>
    public static IReadOnlyList<TranscriptSegment> Split(IReadOnlyList<TranscriptSegment> segments, IReadOnlyList<TimedWord> words, IReadOnlyList<SpeakerTurn> turns)
    {
        if (turns.Count == 0) return segments;
        var byTime = words.OrderBy(word => word.StartMs).ToList();
        var result = new List<TranscriptSegment>();
        var cursor = 0;
        foreach (var segment in segments)
        {
            while (cursor < byTime.Count && Middle(byTime[cursor]) < segment.StartMs) cursor++;
            var inside = new List<TimedWord>();
            for (var i = cursor; i < byTime.Count && Middle(byTime[i]) <= segment.EndMs; i++) inside.Add(byTime[i]);
            if (inside.Count == 0 || !SameText(string.Concat(inside.Select(word => word.Text)), segment.Text))
            {
                result.Add(segment with { Speaker = MostOf(turns, segment.StartMs, segment.EndMs) });
                continue;
            }
            var groups = new List<(string Speaker, List<TimedWord> Words)>();
            foreach (var word in inside)
            {
                var speaker = At(turns, Middle(word))!;
                if (groups.Count > 0 && groups[^1].Speaker == speaker) groups[^1].Words.Add(word);
                else groups.Add((speaker, [word]));
            }
            for (var g = 0; g < groups.Count; g++)
            {
                var start = g == 0 ? segment.StartMs : groups[g].Words[0].StartMs;
                var end = g == groups.Count - 1 ? segment.EndMs : groups[g + 1].Words[0].StartMs;
                result.Add(new TranscriptSegment(start, Math.Max(start, end), string.Concat(groups[g].Words.Select(word => word.Text)).Trim(), groups[g].Speaker));
            }
        }
        return result;
    }

    /// <summary>The speaker who talks most between two moments (the nearest one when nobody talks).</summary>
    public static string MostOf(IReadOnlyList<SpeakerTurn> turns, long start, long end)
    {
        var talk = turns.Select(turn => (turn.Speaker, Overlap: Math.Min(end, turn.EndMs) - Math.Max(start, turn.StartMs))).Where(item => item.Overlap > 0)
            .GroupBy(item => item.Speaker).Select(group => (Speaker: group.Key, Time: group.Sum(item => item.Overlap))).ToList();
        return talk.Count > 0 ? talk.MaxBy(item => item.Time).Speaker : At(turns, (start + end) / 2)!;
    }

    private static long Middle(TimedWord word) => (word.StartMs + word.EndMs) / 2;

    private static long Gap(SpeakerTurn turn, long start, long end) => turn.StartMs < end && start < turn.EndMs ? 0 : Math.Min(Math.Abs(turn.StartMs - end), Math.Abs(start - turn.EndMs));

    private static bool SameText(string words, string text) => string.Concat(words.Where(c => !char.IsWhiteSpace(c))) == string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
}
