using TriAsr.Application;
using TriAsr.Domain;

namespace TriAsr.Audio.Tests;

public sealed class ChunkPlannerTests
{
    private static readonly ChunkOptions Defaults = new();
    private static SpeechSpan S(double startSeconds, double endSeconds) => new((long)Math.Round(startSeconds * 1000), (long)Math.Round(endSeconds * 1000));

    /// <summary>The rules every plan must obey, whatever the input.</summary>
    private static void AssertSound(ChunkPlan plan, IEnumerable<SpeechSpan> speech, ChunkOptions? options = null)
    {
        var o = options ?? Defaults;
        Assert.Equal(ChunkPlan.CurrentVersion, plan.Version);
        if (plan.DurationMs == 0) { Assert.Empty(plan.Chunks); return; }
        Assert.NotEmpty(plan.Chunks);
        Assert.Equal(0, plan.Chunks[0].StartMs);
        Assert.Equal(plan.DurationMs, plan.Chunks[^1].EndMs);
        for (var i = 0; i < plan.Chunks.Count; i++)
        {
            var chunk = plan.Chunks[i];
            Assert.Equal(i, chunk.Index);
            Assert.True(chunk.EndMs > chunk.StartMs, $"chunk {i} is empty");
            // The cap protects the engines' input windows; a long silence is never sent to them, so it may be any length.
            if (chunk.IsSpeech) Assert.True(chunk.DurationMs <= o.HardCapMs, $"chunk {i} is {chunk.DurationMs} ms");
            if (i == 0) continue;
            var previous = plan.Chunks[i - 1];
            if (chunk.OverlapsPrevious)
            {
                Assert.True(chunk.IsSpeech && previous.IsSpeech);
                Assert.Equal(o.OverlapMs, previous.EndMs - chunk.StartMs);
            }
            else Assert.Equal(previous.EndMs, chunk.StartMs);
            Assert.False(!chunk.IsSpeech && !previous.IsSpeech, "two non-speech chunks in a row");
        }
        // A speech chunk holds speech and never a long silence: the engines would be fed (and invent text for) the quiet part.
        var merged = new List<SpeechSpan>();
        foreach (var span in speech.Select(item => new SpeechSpan(Math.Max(0, item.StartMs), Math.Min(plan.DurationMs, item.EndMs)))
                     .Where(item => item.EndMs > item.StartMs).OrderBy(item => item.StartMs))
            if (merged.Count > 0 && span.StartMs <= merged[^1].EndMs) merged[^1] = merged[^1] with { EndMs = Math.Max(merged[^1].EndMs, span.EndMs) };
            else merged.Add(span);
        foreach (var chunk in plan.Chunks.Where(item => item.IsSpeech))
        {
            var inside = merged.Where(span => span.EndMs > chunk.StartMs && span.StartMs < chunk.EndMs).ToList();
            Assert.NotEmpty(inside);
            var cursor = chunk.StartMs;
            foreach (var span in inside)
            {
                Assert.True(Math.Max(span.StartMs, chunk.StartMs) - cursor < o.SeparateSilenceMs, $"chunk {chunk.Index} holds a long silence");
                cursor = Math.Min(span.EndMs, chunk.EndMs);
            }
            Assert.True(chunk.EndMs - cursor < o.SeparateSilenceMs, $"chunk {chunk.Index} ends in a long silence");
        }
        // Every detected sound must sit inside a speech chunk.
        foreach (var span in speech.Where(item => item.EndMs > item.StartMs))
        {
            var start = Math.Max(0, span.StartMs); var end = Math.Min(plan.DurationMs, span.EndMs);
            for (var t = start; t < end; t += Math.Max(1, (end - start) / 40))
                Assert.Contains(plan.Chunks, chunk => chunk.IsSpeech && chunk.StartMs <= t && t < chunk.EndMs);
        }
    }

    [Fact]
    public void RecordingWithoutSpeechIsOneNonSpeechChunk()
    {
        var plan = ChunkPlanner.Plan(95_000, []);
        var chunk = Assert.Single(plan.Chunks);
        Assert.False(chunk.IsSpeech);
        Assert.Equal(new(0, 95_000), (chunk.StartMs, chunk.EndMs));
        Assert.Equal(0, plan.SpeechMs());
    }

    [Fact]
    public void EmptyRecordingHasNoChunks()
    {
        Assert.Empty(ChunkPlanner.Plan(0, [S(0, 1)]).Chunks);
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkPlanner.Plan(-1, []));
    }

    [Fact]
    public void ShortSpeechIsOneSpeechChunkWithShortSilenceAbsorbed()
    {
        var speech = new[] { S(0.8, 6.0) };
        var plan = ChunkPlanner.Plan(7_000, speech);
        AssertSound(plan, speech);
        var chunk = Assert.Single(plan.Chunks);
        Assert.True(chunk.IsSpeech);
        Assert.False(chunk.OverlapsPrevious);
    }

    [Fact]
    public void LongSilencesBecomeNonSpeechChunksAndSpeechKeepsPadding()
    {
        var speech = new[] { S(10, 14), S(60, 64) };
        var plan = ChunkPlanner.Plan(80_000, speech);
        AssertSound(plan, speech);
        Assert.Equal([false, true, false, true, false], plan.Chunks.Select(chunk => chunk.IsSpeech));
        Assert.Equal((9_800, 14_200), (plan.Chunks[1].StartMs, plan.Chunks[1].EndMs));
        Assert.Equal((59_800, 64_200), (plan.Chunks[3].StartMs, plan.Chunks[3].EndMs));
    }

    [Fact]
    public void PhrasesSeparatedByALongSilenceAreNeverPackedIntoOneChunk()
    {
        // 16 s of silence would fit inside a 30 s chunk, but the engines would be fed (and invent text for) the quiet part.
        var speech = new[] { S(10, 14), S(30, 34) };
        var plan = ChunkPlanner.Plan(40_000, speech);
        AssertSound(plan, speech);
        Assert.Equal([false, true, false, true, false], plan.Chunks.Select(chunk => chunk.IsSpeech));
        Assert.Equal(8_800, plan.SpeechMs());
    }

    [Fact]
    public void ShortGapIsSplitInTheMiddleWithNothingLost()
    {
        var speech = new[] { S(1, 10), S(11, 20) };
        var plan = ChunkPlanner.Plan(21_000, speech);
        AssertSound(plan, speech);
        Assert.Single(plan.Chunks); // 1 s gap, 30 s limit: one chunk
        var tight = ChunkPlanner.Plan(21_000, speech, Defaults with { TargetMs = 10_000 });
        AssertSound(tight, speech, Defaults with { TargetMs = 10_000 });
        Assert.Equal(2, tight.Chunks.Count);
        Assert.Equal(10_500, tight.Chunks[0].EndMs);
        Assert.Equal(10_500, tight.Chunks[1].StartMs);
    }

    [Fact]
    public void CutsGoToTheLastSilenceThatKeepsTheChunkWithinTheTarget()
    {
        // Phrases end at 12, 24 and 36 s with 0.5 s silences: the first chunk should take two phrases (24 s), not one or three.
        var speech = new[] { S(0, 12), S(12.5, 24), S(24.5, 36), S(36.5, 40) };
        var plan = ChunkPlanner.Plan(41_000, speech);
        AssertSound(plan, speech);
        Assert.Equal(2, plan.Chunks.Count);
        Assert.InRange(plan.Chunks[0].EndMs, 24_000, 24_500);
        Assert.False(plan.Chunks[1].OverlapsPrevious);
    }

    [Fact]
    public void ASilenceSlightlyBeyondTheTargetBeatsAShortPauseInsideIt()
    {
        // Pause of 0.2 s at 20 s, real silence of 0.4 s at 30.5 s. The cap allows 31 s of speech, so cut at the real silence.
        var speech = new[] { S(0, 20), S(20.2, 30.5), S(30.9, 45) };
        var plan = ChunkPlanner.Plan(46_000, speech);
        AssertSound(plan, speech);
        Assert.InRange(plan.Chunks[0].EndMs, 30_500, 30_900);
    }

    [Fact]
    public void ShortPauseIsUsedWhenNoRealSilenceFits()
    {
        var speech = new[] { S(0, 25), S(25.15, 50) };
        var plan = ChunkPlanner.Plan(51_000, speech);
        AssertSound(plan, speech);
        Assert.Equal(2, plan.Chunks.Count);
        Assert.False(plan.Chunks[1].OverlapsPrevious);
        Assert.InRange(plan.Chunks[0].EndMs, 25_000, 25_150);
    }

    [Fact]
    public void SpeechWithoutAnyPauseIsCutAtTheTargetWithOverlap()
    {
        var speech = new[] { S(0, 100) };
        var plan = ChunkPlanner.Plan(101_000, speech);
        AssertSound(plan, speech);
        Assert.True(plan.Chunks.Count >= 4);
        Assert.All(plan.Chunks.Skip(1).Where(chunk => chunk.IsSpeech), chunk => Assert.True(chunk.OverlapsPrevious));
        Assert.Equal(30_000, plan.Chunks[0].EndMs);
        Assert.Equal(29_000, plan.Chunks[1].StartMs);
        Assert.False(plan.Chunks[0].OverlapsPrevious);
    }

    [Fact]
    public void OverlappingAndUnsortedSpansAreMergedAndOutOfRangeSpansAreClamped()
    {
        var speech = new[] { S(30, 36), S(2, 8), S(6, 12), new SpeechSpan(-500, 300), new SpeechSpan(39_000, 90_000), new SpeechSpan(5_000, 5_000) };
        var plan = ChunkPlanner.Plan(40_000, speech);
        AssertSound(plan, speech);
    }

    [Fact]
    public void PlanningIsDeterministic()
    {
        var speech = Enumerable.Range(0, 200).Select(i => S(i * 4.0, i * 4.0 + 3.1)).ToArray();
        var first = ChunkPlanner.Plan(810_000, speech);
        var second = ChunkPlanner.Plan(810_000, Enumerable.Reverse(speech));
        Assert.Equal(first.Chunks, second.Chunks);
    }

    [Fact]
    public void RandomRecordingsAlwaysProduceSoundPlans()
    {
        var random = new Random(20261002);
        for (var round = 0; round < 400; round++)
        {
            var duration = random.Next(1_000, 900_000);
            var spans = new List<SpeechSpan>(); long cursor = random.Next(0, 5_000);
            while (cursor < duration)
            {
                var length = random.Next(3, round % 3 == 0 ? 90_000 : 12_000);
                var gap = round % 4 == 0 ? random.Next(0, 150) : random.Next(0, 4_000);
                spans.Add(new(cursor, cursor + length)); cursor += length + gap;
            }
            var options = round % 5 == 0 ? Defaults with { TargetMs = 12_000, HardCapMs = 20_000, SeparateSilenceMs = 1_500, PadMs = 300 } : Defaults;
            AssertSound(ChunkPlanner.Plan(duration, spans, options), spans, options);
        }
    }

    [Fact]
    public void ThePlanSurvivesBeingSavedAndLoaded()
    {
        var plan = ChunkPlanner.Plan(70_000, [S(2, 9), S(40, 66)]);
        var restored = System.Text.Json.JsonSerializer.Deserialize<ChunkPlan>(System.Text.Json.JsonSerializer.Serialize(plan))!;
        Assert.Equal(plan.Chunks, restored.Chunks);
        Assert.Equal(plan.SpeechMs(), restored.SpeechMs());
    }

    [Fact]
    public void NonsenseOptionsAreRejected()
    {
        foreach (var options in new[]
        {
            Defaults with { TargetMs = 0 },
            Defaults with { TargetMs = -5 },
            Defaults with { OverlapMs = 30_000 },      // overlap not shorter than the target
            Defaults with { TargetMs = 34_000 },       // target plus padding no longer fits the hard cap
            Defaults with { PadMs = 1_500 },           // more than half of the separating silence
            Defaults with { WeakCutSilenceMs = 400 },  // weaker than the strong cut it should rank below
            Defaults with { SeparateSilenceMs = 250, PadMs = 100 }, // a "long" silence shorter than a good cut
        })
            Assert.Throws<ArgumentException>(() => ChunkPlanner.Plan(10_000, [S(1, 2)], options));
    }
}
