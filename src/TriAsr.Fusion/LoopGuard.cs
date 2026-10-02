using TriAsr.Domain;

namespace TriAsr.Fusion;

/// <summary>A stretch where the recogniser wrote the same segment, or the same few segments in the same order, again and again.</summary>
/// <param name="FirstIndex">Index of the first segment of the first copy in the segment list.</param>
/// <param name="Period">How many segments make up one copy of the pattern (1 for a single repeated phrase).</param>
/// <param name="Segments">All segments in the run, including the first copy.</param>
/// <param name="Text">The first copy, the segments joined with " / ".</param>
public sealed record LoopRun(int FirstIndex, int Period, int Segments, string Text, long StartMs, long EndMs)
{
    public int Copies => Segments / Period;
    /// <summary>How many segments <see cref="LoopGuard.Remove"/> takes out of this run: everything after the first copy.</summary>
    public int Removed => Segments - Period;
}

/// <summary>
/// Finds and removes runaway repetition in a speech recogniser's output: the same segment, or a short pattern of up to
/// <see cref="MaximumPeriod"/> segments, written over and over, back to back, for a long time. That is a well-known failure of
/// Whisper over stretches without speech and over the end of a song (each window is conditioned on the previous text, so a repeated
/// phrase feeds itself). A fresh run does not repair it - it invents different text - so the repeats are removed and the first
/// copy is kept.
/// <para>
/// The rule is strict on purpose: at least <see cref="MinimumCopies"/> copies of the pattern in a row, every segment starting within
/// <see cref="MaximumGapMs"/> of the end of the one before, covering at least <see cref="MinimumSpanMs"/>. Real measurements: a sung
/// chorus, a chant and ordinary speech never produced three identical segments in a row; the broken runs had 830 (one phrase, a
/// game video) and 76 (two lines alternating 38 times, the end of a song).
/// </para>
/// </summary>
public static class LoopGuard
{
    public const int MinimumCopies = 8;
    public const int MaximumPeriod = 4;
    public const long MinimumSpanMs = 12_000;
    public const long MaximumGapMs = 1_000;

    public static IReadOnlyList<LoopRun> Find(IReadOnlyList<TranscriptSegment> segments)
    {
        var runs = new List<LoopRun>();
        var keys = segments.Select(segment => TranscriptQuality.Normalize(segment.Text)).ToArray();
        var start = 0;
        while (start < segments.Count)
        {
            LoopRun? found = null;
            for (var period = 1; period <= MaximumPeriod && found is null; period++)
            {
                if (start + period * MinimumCopies > segments.Count) break;
                if (Enumerable.Range(start, period).Any(index => keys[index].Length == 0)) continue;
                // The pattern holds as long as every segment equals the one a period earlier and follows its predecessor without a pause.
                var end = start + period - 1;
                for (var index = start + 1; index < start + period; index++)
                    if (segments[index].StartMs - segments[index - 1].EndMs > MaximumGapMs) { end = -1; break; }
                if (end < 0) continue;
                while (end + 1 < segments.Count && keys[end + 1].Length > 0 && keys[end + 1] == keys[end + 1 - period]
                       && segments[end + 1].StartMs - segments[end].EndMs <= MaximumGapMs) end++;
                var length = end - start + 1;
                if (length / period < MinimumCopies || segments[end].EndMs - segments[start].StartMs < MinimumSpanMs) continue;
                // A pattern of period 2 or more must really alternate; one phrase repeated is the period 1 case.
                if (period > 1 && Enumerable.Range(start, period).Select(index => keys[index]).Distinct().Count() == 1) continue;
                found = new(start, period, length, string.Join(" / ", Enumerable.Range(start, period).Select(index => segments[index].Text.Trim())),
                    segments[start].StartMs, segments[end].EndMs);
            }
            if (found is null) { start++; continue; }
            runs.Add(found);
            start = found.FirstIndex + found.Segments;
        }
        return runs;
    }

    /// <summary>The segments without the repeated copies (the first copy of every run stays), and what was removed.</summary>
    public static (IReadOnlyList<TranscriptSegment> Kept, IReadOnlyList<LoopRun> Runs) Remove(IReadOnlyList<TranscriptSegment> segments)
    {
        var runs = Find(segments);
        if (runs.Count == 0) return (segments, runs);
        var drop = new HashSet<int>();
        foreach (var run in runs)
            for (var index = run.FirstIndex + run.Period; index < run.FirstIndex + run.Segments; index++) drop.Add(index);
        return (segments.Where((_, index) => !drop.Contains(index)).ToArray(), runs);
    }
}
