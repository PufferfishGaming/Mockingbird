using TriAsr.Domain;
using TriAsr.Fusion;

namespace TriAsr.Fusion.Tests;

public sealed class LoopGuardTests
{
    private static TranscriptSegment S(double startSeconds, double endSeconds, string text) =>
        new((long)Math.Round(startSeconds * 1000), (long)Math.Round(endSeconds * 1000), text);

    /// <summary>A loop as seen in a real 28 minute recording: the same line every 2.0 s, back to back.</summary>
    private static List<TranscriptSegment> Loop(double start, int copies, string text = "Egy kicsit, hogy mi történik.", double length = 2)
        => Enumerable.Range(0, copies).Select(i => S(start + i * length, start + (i + 1) * length, text)).ToList();

    [Fact]
    public void ALongRunOfTheSameSegmentIsReducedToItsFirstCopy()
    {
        var segments = new List<TranscriptSegment> { S(2, 6, "Ez itt a Trials SMP.") };
        segments.AddRange(Loop(60, 400));
        segments.Add(S(900, 905, "Aztán jött a sárkány."));
        var (kept, runs) = LoopGuard.Remove(segments);
        var run = Assert.Single(runs);
        Assert.Equal((1, 400), (run.FirstIndex, run.Copies));
        Assert.Equal(["Ez itt a Trials SMP.", "Egy kicsit, hogy mi történik.", "Aztán jött a sárkány."], kept.Select(segment => segment.Text));
        Assert.Equal(60_000, kept[1].StartMs);          // the copy that stays is the first one, with its own time
        Assert.Equal(1 + 400 + 1, segments.Count);       // the input list is not modified
    }

    [Fact]
    public void CapitalsAndPunctuationDoNotHideACopy()
    {
        var segments = Loop(0, 5, "Thank you.");
        segments.AddRange(Loop(10, 5, "thank you!"));
        segments.AddRange(Loop(20, 5, "Thank you"));
        Assert.Equal(15, Assert.Single(LoopGuard.Find(segments)).Copies);
    }

    [Fact]
    public void ARunThatIsTooShortInCopiesOrInTimeIsLeftAlone()
    {
        Assert.Empty(LoopGuard.Find(Loop(0, LoopGuard.MinimumCopies - 1, length: 5)));   // 7 copies over 35 s
        Assert.Empty(LoopGuard.Find(Loop(0, 12, length: 0.5)));                          // 12 copies but only 6 s: a chant
        Assert.Single(LoopGuard.Find(Loop(0, 8, length: 1.5)));                          // 8 copies over exactly 12 s is the smallest loop
    }

    [Fact]
    public void ARealPauseBreaksTheRun()
    {
        // Two groups of seven with a 3 s silence between them: never eight in a row.
        var segments = Loop(0, 7, length: 3);
        segments.AddRange(Loop(24, 7, length: 3));
        Assert.Empty(LoopGuard.Find(segments));
        // The same with the groups touching is a loop.
        var touching = Loop(0, 7, length: 3);
        touching.AddRange(Loop(21, 7, length: 3));
        Assert.Equal(14, Assert.Single(LoopGuard.Find(touching)).Copies);
    }

    [Fact]
    public void AShortAlternationIsNotALoop()
    {
        // Seven back-to-back alternations: fewer than the eight copies a loop needs.
        var segments = new List<TranscriptSegment>();
        for (var i = 0; i < 14; i++) segments.Add(S(i * 3, i * 3 + 3, i % 2 == 0 ? "I found a love for me" : "Darling, just dive right in"));
        Assert.Empty(LoopGuard.Find(segments));
        Assert.Same(segments, LoopGuard.Remove(segments).Kept); // nothing found: the very same list comes back
    }

    [Fact]
    public void GenuineRepetitionWithPausesBetweenTheCopiesIsKept()
    {
        // "No, no, no ..." said ten times with a breath between: each copy starts 2 s after the previous one ended.
        var segments = Enumerable.Range(0, 10).Select(i => S(i * 4, i * 4 + 2, "Nem, nem.")).ToList();
        Assert.Empty(LoopGuard.Find(segments));
    }

    [Fact]
    public void EmptyAndBlankSegmentsNeverFormARun()
    {
        Assert.Empty(LoopGuard.Find(Loop(0, 30, "")));
        Assert.Empty(LoopGuard.Find(Loop(0, 30, " ... ")));
    }

    [Fact]
    public void TwoSeparateLoopsAreBothReduced()
    {
        var segments = Loop(0, 10);
        segments.Add(S(20, 25, "Real speech in between."));
        segments.AddRange(Loop(25, 10, "Köszönöm."));
        var (kept, runs) = LoopGuard.Remove(segments);
        Assert.Equal([10, 10], runs.Select(run => run.Copies));
        Assert.Equal(["Egy kicsit, hogy mi történik.", "Real speech in between.", "Köszönöm."], kept.Select(segment => segment.Text));
    }

    [Fact]
    public void RemovingTwiceChangesNothingFurther()
    {
        var segments = Loop(0, 200);
        var once = LoopGuard.Remove(segments).Kept;
        Assert.Empty(LoopGuard.Find(once));
        Assert.Equal(once, LoopGuard.Remove(once).Kept);
    }

    [Fact]
    public void EmptyInputIsFine()
    {
        Assert.Empty(LoopGuard.Find([]));
        Assert.Empty(LoopGuard.Remove([]).Kept);
    }

    [Fact]
    public void WhateverTheInputOnlyRepeatsOfAQualifyingRunAreRemovedAndOrderIsKept()
    {
        var random = new Random(3);
        var words = new[] { "ja", "nein", "doch", "gut" };
        for (var round = 0; round < 300; round++)
        {
            var segments = new List<TranscriptSegment>(); double time = 0;
            for (var i = 0; i < random.Next(0, 80); i++)
            {
                var length = random.NextDouble() * 4 + 0.2; var gap = random.NextDouble() < 0.7 ? 0 : random.NextDouble() * 3;
                segments.Add(S(time + gap, time + gap + length, words[random.Next(random.Next(1, 5))]));
                time += gap + length;
            }
            var (kept, runs) = LoopGuard.Remove(segments);
            Assert.Equal(segments.Count - runs.Sum(run => run.Removed), kept.Count);
            Assert.Equal(kept.OrderBy(segment => segment.StartMs), kept);
            Assert.All(kept, segment => Assert.Contains(segment, segments));
            Assert.All(runs, run => { Assert.True(run.Copies >= LoopGuard.MinimumCopies); Assert.True(run.EndMs - run.StartMs >= LoopGuard.MinimumSpanMs); });
        }
    }

    // ---- patterns of several segments (two lines alternating until the end of a song, measured on a real recording) -----------------

    /// <summary>Segments that repeat the given texts in order, each as long as given, back to back.</summary>
    private static List<TranscriptSegment> Pattern(double start, int cycles, params (string Text, double Length)[] block)
    {
        var segments = new List<TranscriptSegment>(); var time = start;
        for (var cycle = 0; cycle < cycles; cycle++)
            foreach (var (text, length) in block) { segments.Add(S(time, time + length, text)); time += length; }
        return segments;
    }

    [Fact]
    public void TwoLinesAlternatingUntilTheEndOfASongAreReducedToTheFirstPair()
    {
        // Shaped like the real recording: the song is transcribed normally, then two lines (1.0 s and 3.9 s long) alternate 38 times.
        var segments = Enumerable.Range(0, 12).Select(i => S(i * 15, i * 15 + 12, $"line {i} of the song")).ToList();
        segments.AddRange(Pattern(186, 38, ("first repeated line of the ending", 1.0), ("second repeated line", 3.9)));
        var (kept, runs) = LoopGuard.Remove(segments);
        var run = Assert.Single(runs);
        Assert.Equal((12, 2, 76, 38, 74), (run.FirstIndex, run.Period, run.Segments, run.Copies, run.Removed));
        Assert.Equal("first repeated line of the ending / second repeated line", run.Text);
        Assert.Equal(12 + 2, kept.Count);
        Assert.Equal(["first repeated line of the ending", "second repeated line"], kept.Skip(12).Select(segment => segment.Text));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void PatternsUpToFourSegmentsAreFound(int period)
    {
        var block = Enumerable.Range(0, period).Select(i => ($"phrase {i}", 1.5)).ToArray();
        var segments = Pattern(0, 10, block);
        var run = Assert.Single(LoopGuard.Find(segments));
        Assert.Equal((period, 10 * period), (run.Period, run.Segments));
        Assert.Equal(period, LoopGuard.Remove(segments).Kept.Count);
    }

    [Fact]
    public void ARepeatedPatternThatEndsHalfWayThroughACycleIsRemovedToo()
    {
        var segments = Pattern(0, 9, ("alpha", 2), ("beta", 2));
        segments.Add(S(36, 38, "alpha"));
        var run = Assert.Single(LoopGuard.Find(segments));
        Assert.Equal(19, run.Segments);
        Assert.Equal(2, LoopGuard.Remove(segments).Kept.Count);
    }

    [Fact]
    public void AChorusSungThreeTimesIsNotALoop()
    {
        var chorus = new[] { ("the first chorus line", 3.0), ("the second chorus line", 3.0), ("the third chorus line", 3.0) };
        var segments = Pattern(0, 3, chorus);                                            // three cycles back to back
        segments.AddRange(Pattern(27, 1, ("a bridge that is new", 6)));
        segments.AddRange(Pattern(33, 3, chorus));
        Assert.Empty(LoopGuard.Find(segments));
    }

    [Fact]
    public void ALongPatternIsNotGuessedAt()
    {
        var block = Enumerable.Range(0, LoopGuard.MaximumPeriod + 1).Select(i => ($"phrase {i}", 1.5)).ToArray();
        Assert.Empty(LoopGuard.Find(Pattern(0, 12, block)));
    }

    [Fact]
    public void AnAlternatingPatternWithPausesIsNotALoop()
    {
        var segments = new List<TranscriptSegment>();
        for (var i = 0; i < 24; i++) segments.Add(S(i * 5, i * 5 + 2, i % 2 == 0 ? "yes" : "no")); // 3 s of quiet between every two
        Assert.Empty(LoopGuard.Find(segments));
    }

    [Fact]
    public void SpeechAroundAPatternLoopIsKeptInOrder()
    {
        var segments = new List<TranscriptSegment> { S(0, 5, "Opening words.") };
        segments.AddRange(Pattern(5, 12, ("x", 1.5), ("y", 1.5)));
        segments.Add(S(41, 46, "Closing words."));
        var kept = LoopGuard.Remove(segments).Kept;
        Assert.Equal(["Opening words.", "x", "y", "Closing words."], kept.Select(segment => segment.Text));
    }

    [Fact]
    public void ASinglePhraseRunIsStillReportedAsPeriodOne()
    {
        var run = Assert.Single(LoopGuard.Find(Loop(0, 30)));
        Assert.Equal((1, 30, 30, 29), (run.Period, run.Segments, run.Copies, run.Removed));
    }
}
