using TriAsr.Domain;
using TriAsr.Fusion;

namespace TriAsr.Fusion.Tests;

public sealed class UnsupportedTextGuardTests
{
    private static TranscriptSegment S(double startSeconds, double endSeconds, string text) =>
        new((long)Math.Round(startSeconds * 1000), (long)Math.Round(endSeconds * 1000), text);

    /// <summary>Speech from 0 to 10 s and 50 to 60 s, nothing detected in between.</summary>
    private static readonly AudioChunk[] Chunks =
    [
        new(0, 0, 10_000, true, false), new(1, 10_000, 50_000, false, false), new(2, 50_000, 60_000, true, false),
    ];
    private const string Canary = "Hallo zusammen das ist der Anfang und hier ist das Ende";

    [Fact]
    public void TextWithNoDetectedSpeechAndNoSupportFromTheOtherEngineIsLeftOut()
    {
        var segments = new List<TranscriptSegment> { S(1, 5, "Hallo zusammen das ist der Anfang"), S(55, 58, "Untertitel der Amara org Community"), S(20, 22, "Vertraue und glaube es hilft") };
        segments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        var (kept, removed) = UnsupportedTextGuard.Remove(segments, Canary, Chunks);
        Assert.Equal(["Hallo zusammen das ist der Anfang", "Untertitel der Amara org Community"], kept.Select(segment => segment.Text));
        var gone = Assert.Single(removed);
        Assert.Equal((1, 20_000L, 22_000L, 0.2, 0.0), (gone.Index, gone.StartMs, gone.EndMs, gone.Support, gone.PairSupport)); // "und" is the only word Canary also wrote
        Assert.Equal("Vertraue und glaube es hilft", gone.Text);
    }

    [Fact]
    public void ASungLineThatTheOtherEngineAlsoWroteIsKept()
    {
        // No speech was detected, but Canary has the words: this is singing, not an invention.
        var segments = new[] { S(20, 24, "das ist hier das Ende") };
        Assert.Same(segments, UnsupportedTextGuard.Remove(segments, Canary, Chunks).Kept);
    }

    [Fact]
    public void TextWhereSpeechWasDetectedIsKeptEvenWithoutSupport()
    {
        var segments = new[] { S(52, 56, "Etwas ganz anderes als Canary") };
        Assert.Same(segments, UnsupportedTextGuard.Remove(segments, Canary, Chunks).Kept);
    }

    [Fact]
    public void ASegmentThatReachesIntoDetectedSpeechIsKept()
    {
        var segments = new[] { S(8, 14, "Etwas ganz anderes als Canary") };      // starts inside the speech chunk
        Assert.Empty(UnsupportedTextGuard.Remove(segments, Canary, Chunks).Removed);
    }

    [Fact]
    public void AFewMillisecondsOfJitterAtTheEdgeDoNotMatter()
    {
        var inside = new[] { S(9.85, 14, "Vertraue und glaube es hilft") };     // 150 ms before the quiet stretch starts
        Assert.Single(UnsupportedTextGuard.Remove(inside, Canary, Chunks).Removed);
        var outside = new[] { S(9.5, 14, "Vertraue und glaube es hilft") };     // 500 ms before: that is speech
        Assert.Empty(UnsupportedTextGuard.Remove(outside, Canary, Chunks).Removed);
    }

    [Fact]
    public void ShortSegmentsAreNeverJudged()
    {
        var segments = new[] { S(20, 21, "Mm-hm"), S(30, 31, "Nein nein") };
        Assert.Empty(UnsupportedTextGuard.Remove(segments, Canary, Chunks).Removed);
    }

    [Fact]
    public void ExactlyHalfSupportedIsKeptAndLessIsNot()
    {
        Assert.Empty(UnsupportedTextGuard.Remove([S(20, 24, "das ist fremd erfunden")], Canary, Chunks).Removed);      // 2 of 4 words
        Assert.Single(UnsupportedTextGuard.Remove([S(20, 24, "das fremd erfunden neu")], Canary, Chunks).Removed);     // 1 of 4 words
    }

    [Fact]
    public void WordsThatAllOccurButNeverNextToEachOtherAreNotSupport()
    {
        // Every word is somewhere in the other text (a long text contains most common words), but never in this order: not the same speech.
        var scrambled = new[] { S(20, 24, "Anfang Ende hier Hallo") };
        var gone = Assert.Single(UnsupportedTextGuard.Remove(scrambled, Canary, Chunks).Removed);
        Assert.Equal((1.0, 0.0), (gone.Support, gone.PairSupport));
        // The same words in the other engine's order are the same speech.
        Assert.Empty(UnsupportedTextGuard.Remove([S(20, 24, "das ist der Anfang und hier")], Canary, Chunks).Removed);
    }

    [Fact]
    public void ALineWhereTheOtherEngineDiffersInOneWordIsKept()
    {
        // Canary heard "ist der Anfang" where Whisper wrote "ist ein Anfang": 3 of 4 pairs still match.
        Assert.Empty(UnsupportedTextGuard.Remove([S(20, 24, "das ist ein Anfang und hier")], Canary, Chunks).Removed);
    }

    [Fact]
    public void CapitalsAndPunctuationDoNotHideSupport()
    {
        Assert.Empty(UnsupportedTextGuard.Remove([S(20, 24, "HALLO, Zusammen: das ist!")], Canary, Chunks).Removed);
    }

    [Fact]
    public void WithoutASecondOpinionNothingIsRemoved()
    {
        var segments = new[] { S(20, 24, "Vertraue und glaube es hilft") };
        Assert.Same(segments, UnsupportedTextGuard.Remove(segments, "", Chunks).Kept);
        Assert.Same(segments, UnsupportedTextGuard.Remove(segments, "  ... ", Chunks).Kept);
        Assert.Same(segments, UnsupportedTextGuard.Remove(segments, Canary, []).Kept);
    }

    [Fact]
    public void WhateverTheInputTheResultIsAnOrderedSubsetAndTheCountsAdd()
    {
        var random = new Random(5);
        var vocabulary = new[] { "das", "ist", "hier", "Ende", "Anfang", "fremd", "neu", "Haus", "Baum" };
        for (var round = 0; round < 300; round++)
        {
            var segments = Enumerable.Range(0, random.Next(0, 40)).Select(i => S(i * 1.5, i * 1.5 + 1.2, string.Join(' ', Enumerable.Range(0, random.Next(1, 7)).Select(_ => vocabulary[random.Next(vocabulary.Length)])))).ToList();
            var (kept, removed) = UnsupportedTextGuard.Remove(segments, Canary, Chunks);
            Assert.Equal(segments.Count, kept.Count + removed.Count);
            Assert.All(kept, segment => Assert.Contains(segment, segments));
            Assert.Equal(kept.OrderBy(segment => segment.StartMs), kept);
            Assert.All(removed, item => Assert.True((item.Support < UnsupportedTextGuard.MinimumSupport || item.PairSupport < UnsupportedTextGuard.MinimumPairSupport) && item.Text.Split(' ').Length >= UnsupportedTextGuard.MinimumWords));
        }
    }
}
