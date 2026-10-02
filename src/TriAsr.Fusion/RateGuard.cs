using TriAsr.Domain;

namespace TriAsr.Fusion;

/// <summary>A cluster of segments written at an impossible speaking speed, all with the same collapsed length.</summary>
/// <param name="FastSegments">How many segments in the cluster were too fast.</param>
/// <param name="Removed">How many of them were exact repeats of a line already kept in the cluster.</param>
public sealed record FastRun(int FirstIndex, int LastIndex, int FastSegments, int Removed, long StartMs, long EndMs);

/// <summary>
/// Whisper sometimes loses its timestamps over the end of a song and writes lines of five to seven words into segments of exactly
/// one second each, repeating the same lines again and again. The repeats are not in a pattern the <see cref="LoopGuard"/> can match
/// (the block is longer than four segments and shows up fewer than eight times), but the speed gives them away: nobody sings
/// five words in one second, and genuine fast speech does not come out as dozens of segments of the same length.
/// <para>
/// A cluster qualifies only when it has at least <see cref="MinimumSegments"/> segments of at least <see cref="MinimumWords"/> words
/// at <see cref="FastWordsPerSecond"/> or faster, starting no more than <see cref="MaximumClusterGapSeconds"/> after the previous one,
/// and at least <see cref="SameDurationShare"/> of them share one length (to a tenth of a second). In a qualifying cluster each line is
/// kept once and its later exact repeats are removed; lines that appear only once are kept, because the words may be genuine even
/// when their timestamps are not. Measured: on every Whisper output available (speech, a German clip, skipping-mode transcripts)
/// the fastest ordinary segment was 3.9 words per second and no cluster qualified; on the song 25 segments qualified.
/// </para>
/// </summary>
public static class RateGuard
{
    public const int MinimumWords = 5;
    public const double FastWordsPerSecond = 4.5;
    public const int MinimumSegments = 8;
    public const double MaximumClusterGapSeconds = 6;
    public const double SameDurationShare = 0.75;

    public static IReadOnlyList<FastRun> Find(IReadOnlyList<TranscriptSegment> segments) => Analyze(segments).Runs;

    /// <summary>The segments without the repeated lines, and what was removed.</summary>
    public static (IReadOnlyList<TranscriptSegment> Kept, IReadOnlyList<FastRun> Runs) Remove(IReadOnlyList<TranscriptSegment> segments)
    {
        var (runs, drop) = Analyze(segments);
        if (runs.Count == 0) return (segments, runs);
        return (segments.Where((_, index) => !drop.Contains(index)).ToArray(), runs);
    }

    private static (IReadOnlyList<FastRun> Runs, HashSet<int> Drop) Analyze(IReadOnlyList<TranscriptSegment> segments)
    {
        var runs = new List<FastRun>(); var drop = new HashSet<int>();
        var keys = segments.Select(segment => TranscriptQuality.Normalize(segment.Text)).ToArray();
        var fast = new List<int>();
        for (var index = 0; index < segments.Count; index++)
        {
            var seconds = (segments[index].EndMs - segments[index].StartMs) / 1000d;
            var words = keys[index].Length == 0 ? 0 : keys[index].Split(' ').Length;
            if (words >= MinimumWords && seconds > 0 && words / seconds >= FastWordsPerSecond) fast.Add(index);
        }
        var cluster = new List<int>();
        void Close()
        {
            if (cluster.Count >= MinimumSegments && SharedLength(segments, cluster) >= SameDurationShare)
            {
                var seen = new HashSet<string>(); var removed = 0;
                foreach (var index in cluster)
                    if (!seen.Add(keys[index])) { drop.Add(index); removed++; }
                if (removed > 0) runs.Add(new(cluster[0], cluster[^1], cluster.Count, removed, segments[cluster[0]].StartMs, segments[cluster[^1]].EndMs));
            }
            cluster.Clear();
        }
        foreach (var index in fast)
        {
            if (cluster.Count > 0 && (segments[index].StartMs - segments[cluster[^1]].EndMs) / 1000d > MaximumClusterGapSeconds) Close();
            cluster.Add(index);
        }
        Close();
        return (runs, drop);
    }

    /// <summary>The share of the cluster's segments that have the most common length (rounded to a tenth of a second).</summary>
    private static double SharedLength(IReadOnlyList<TranscriptSegment> segments, List<int> cluster) =>
        cluster.GroupBy(index => Math.Round((segments[index].EndMs - segments[index].StartMs) / 1000d, 1)).Max(group => group.Count()) / (double)cluster.Count;
}
