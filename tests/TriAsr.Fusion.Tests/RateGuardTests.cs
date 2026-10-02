using TriAsr.Domain;
using TriAsr.Fusion;

namespace TriAsr.Fusion.Tests;

public sealed class RateGuardTests
{
    private static TranscriptSegment S(double startSeconds, double lengthSeconds, string text) =>
        new((long)Math.Round(startSeconds * 1000), (long)Math.Round((startSeconds + lengthSeconds) * 1000), text);

    /// <summary>Ordinary, slow segments: six words in four seconds.</summary>
    private static List<TranscriptSegment> Ordinary(int count, double start = 0) =>
        Enumerable.Range(0, count).Select(i => S(start + i * 5, 4, $"this ordinary line number {i} is slow")).ToList();

    /// <summary>The shape measured on a real recording: the same few lines again and again, six words in exactly one second.</summary>
    private static List<TranscriptSegment> Collapsed(double start, int lines, int copies, double step = 3)
    {
        var segments = new List<TranscriptSegment>(); var time = start;
        for (var copy = 0; copy < copies; copy++)
            for (var line = 0; line < lines; line++) { segments.Add(S(time, 1.0, $"hallucinated line {line} of the ending")); time += step; }
        return segments;
    }

    [Fact]
    public void ACollapsedStretchKeepsEachDistinctLineOnce()
    {
        var segments = Ordinary(10);
        segments.AddRange(Collapsed(60, lines: 7, copies: 4));
        segments.AddRange(Ordinary(2, start: 200));
        var (kept, runs) = RateGuard.Remove(segments);
        var run = Assert.Single(runs);
        Assert.Equal((10, 37, 28, 21), (run.FirstIndex, run.LastIndex, run.FastSegments, run.Removed));
        Assert.Equal(10 + 7 + 2, kept.Count);
        Assert.Equal(Enumerable.Range(0, 7).Select(line => $"hallucinated line {line} of the ending"), kept.Skip(10).Take(7).Select(segment => segment.Text));
        Assert.Equal(segments.Take(10), kept.Take(10));          // the ordinary segments before it are untouched
        Assert.Equal(segments.TakeLast(2), kept.TakeLast(2));    // and after it
        Assert.Equal(10 + 28 + 2, segments.Count);                // the input list is not modified
    }

    [Fact]
    public void FastSpeechWhoseSegmentsDifferInLengthIsLeftAlone()
    {
        // Twelve segments of five to six words at more than five words per second, but every one a different length: a fast talker, not a collapse.
        var segments = Enumerable.Range(0, 12).Select(i => S(i * 3, 0.8 + i * 0.1, $"fast but real sentence number {i} here")).ToList();
        Assert.Empty(RateGuard.Find(segments));
        Assert.Same(segments, RateGuard.Remove(segments).Kept);
    }

    [Fact]
    public void FewerThanEightFastSegmentsAreLeftAlone()
    {
        Assert.Empty(RateGuard.Find(Collapsed(0, lines: 7, copies: 1)));                                  // 7 fast segments
        Assert.Empty(RateGuard.Find(Collapsed(0, lines: 1, copies: RateGuard.MinimumSegments - 1)));
        Assert.Single(RateGuard.Find(Collapsed(0, lines: 4, copies: 2)));                                 // 8 fast segments, 4 repeated
    }

    [Fact]
    public void AnIsolatedFastSegmentIsLeftAlone()
    {
        var segments = Ordinary(20);
        segments.Insert(10, S(52, 0.9, "one quick sentence in a hurry here"));
        Assert.Empty(RateGuard.Find(segments));
    }

    [Fact]
    public void ACollapsedStretchWithoutAnyRepeatChangesNothing()
    {
        // Timestamps collapsed, but every line is different: the words may all be genuine, so none are removed.
        var segments = Enumerable.Range(0, 12).Select(i => S(i * 3, 1.0, $"unique collapsed line number {i} here")).ToList();
        Assert.Empty(RateGuard.Find(segments));
        Assert.Same(segments, RateGuard.Remove(segments).Kept);
    }

    [Fact]
    public void SlowRepetitionIsNotTheBusinessOfThisGuard()
    {
        // Forty segments of five words over four seconds each (1.25 words per second) that repeat three lines.
        var segments = Enumerable.Range(0, 40).Select(i => S(i * 5, 4, $"a slow repeated line {i % 3} again")).ToList();
        Assert.Empty(RateGuard.Find(segments));
    }

    [Fact]
    public void TwoClustersFarApartAreJudgedSeparately()
    {
        var segments = Collapsed(0, lines: 4, copies: 3);                       // cluster 1: 12 segments over 36 s
        segments.AddRange(Collapsed(120, lines: 4, copies: 3));                 // cluster 2: the same lines again, 84 s later
        var (kept, runs) = RateGuard.Remove(segments);
        Assert.Equal([8, 8], runs.Select(run => run.Removed));
        Assert.Equal(8, kept.Count);                                            // 4 lines kept in each cluster, not 4 in total
    }

    [Fact]
    public void RemovingTwiceChangesNothingFurther()
    {
        var once = RateGuard.Remove(Collapsed(0, lines: 7, copies: 4)).Kept;
        Assert.Empty(RateGuard.Find(once));
        Assert.Equal(once, RateGuard.Remove(once).Kept);
    }

    [Fact]
    public void EmptyInputAndEmptyTextAreFine()
    {
        Assert.Empty(RateGuard.Find([]));
        Assert.Empty(RateGuard.Find(Enumerable.Range(0, 20).Select(i => S(i * 3, 1.0, "")).ToList()));
        Assert.Empty(RateGuard.Find(Enumerable.Range(0, 20).Select(i => new TranscriptSegment(i * 3000, i * 3000, "zero length segment of words here")).ToList()));
    }

    [Fact]
    public void WhateverTheInputOnlyRepeatsInsideQualifyingClustersAreRemovedAndOrderIsKept()
    {
        var random = new Random(11);
        for (var round = 0; round < 300; round++)
        {
            var segments = new List<TranscriptSegment>(); double time = 0;
            for (var i = 0; i < random.Next(0, 90); i++)
            {
                var fast = random.NextDouble() < 0.5;
                var length = fast ? (random.NextDouble() < 0.7 ? 1.0 : 0.8 + random.NextDouble()) : 2 + random.NextDouble() * 3;
                var words = string.Join(' ', Enumerable.Range(0, random.Next(1, 8)).Select(_ => "w" + random.Next(3)));
                segments.Add(S(time, length, words)); time += length + random.NextDouble() * 4;
            }
            var (kept, runs) = RateGuard.Remove(segments);
            Assert.Equal(segments.Count - runs.Sum(run => run.Removed), kept.Count);
            Assert.All(kept, segment => Assert.Contains(segment, segments));
            Assert.Equal(kept.OrderBy(segment => segment.StartMs), kept);
            Assert.All(runs, run => Assert.True(run.FastSegments >= RateGuard.MinimumSegments && run.Removed > 0));
        }
    }
}
