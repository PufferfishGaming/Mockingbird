using TriAsr.Domain;

namespace TriAsr.Fusion;

/// <summary>A stretch where the recogniser wrote the same segment again and again.</summary>
/// <param name="FirstIndex">Index of the first copy in the segment list.</param>
public sealed record LoopRun(int FirstIndex, int Copies, string Text, long StartMs, long EndMs);

/// <summary>
/// Finds and removes runaway repetition in a speech recogniser's output: the same segment written over and over, back to back, for
/// a long time. That is a well-known failure of Whisper over stretches without speech (each window is conditioned on the previous
/// text, so one repeated phrase feeds itself). A fresh run does not repair it - it invents different text - so the repeats are
/// removed and the first copy is kept.
/// <para>
/// The rule is strict on purpose, because songs and chants repeat genuinely: at least <see cref="MinimumCopies"/> identical
/// segments in a row, each starting within <see cref="MaximumGapMs"/> of the end of the one before, covering at least
/// <see cref="MinimumSpanMs"/>. On every recording measured, a sung chorus, a chant or ordinary speech never produced three identical
/// segments in a row, while the broken run had 830.
/// </para>
/// </summary>
public static class LoopGuard
{
    public const int MinimumCopies = 8;
    public const long MinimumSpanMs = 12_000;
    public const long MaximumGapMs = 1_000;

    public static IReadOnlyList<LoopRun> Find(IReadOnlyList<TranscriptSegment> segments)
    {
        var runs = new List<LoopRun>();
        var keys = segments.Select(segment => TranscriptQuality.Normalize(segment.Text)).ToArray();
        var start = 0;
        while (start < segments.Count)
        {
            var end = start;
            while (keys[start].Length > 0 && end + 1 < segments.Count && keys[end + 1] == keys[start]
                   && segments[end + 1].StartMs - segments[end].EndMs <= MaximumGapMs) end++;
            var copies = end - start + 1;
            if (copies >= MinimumCopies && segments[end].EndMs - segments[start].StartMs >= MinimumSpanMs)
                runs.Add(new(start, copies, segments[start].Text.Trim(), segments[start].StartMs, segments[end].EndMs));
            start = end + 1;
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
            for (var index = run.FirstIndex + 1; index < run.FirstIndex + run.Copies; index++) drop.Add(index);
        return (segments.Where((_, index) => !drop.Contains(index)).ToArray(), runs);
    }
}
