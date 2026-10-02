using TriAsr.Application;
using TriAsr.Domain;

namespace TriAsr.Audio.Tests;

public sealed class EngineWindowTests
{
    private static ChunkPlan Plan(params AudioChunk[] chunks) => new(ChunkPlan.CurrentVersion, chunks[^1].EndMs, "test", chunks);
    private static AudioChunk C(int index, long start, long end, bool speech, bool overlap = false) => new(index, start, end, speech, overlap);
    private static readonly ChunkPlan Mixed = Plan(C(0, 0, 9_800, false), C(1, 9_800, 14_200, true), C(2, 14_200, 29_800, false), C(3, 29_800, 34_200, true), C(4, 34_200, 40_000, false));

    [Fact]
    public void SkippingKeepsOnlySpeechAndKeepsTheChunkNumbers()
    {
        var windows = CanaryWindow.SpeechWindows(Mixed, 40_000)!;
        Assert.Equal([1, 3], windows.Select(window => window.Index));
        Assert.All(windows, window => Assert.True(window.IsSpeech));
        Assert.Equal((9_800, 14_200), (windows[0].StartMs, windows[0].EndMs));
    }

    [Fact]
    public void AnOverlapIsCarriedToTheWindow()
    {
        var plan = Plan(C(0, 0, 30_000, true), C(1, 29_000, 50_000, true, overlap: true));
        Assert.Equal([false, true], CanaryWindow.SpeechWindows(plan, 50_000)!.Select(window => window.OverlapsPrevious));
    }

    [Fact]
    public void APlanForAnotherRecordingOrFormatIsNotUsed()
    {
        Assert.Null(CanaryWindow.SpeechWindows(Mixed, 41_000));
        Assert.NotNull(CanaryWindow.SpeechWindows(Mixed, 40_002)); // a rounding difference of a few samples is fine
        Assert.Null(CanaryWindow.SpeechWindows(Mixed with { Version = 99 }, 40_000));
    }

    [Fact]
    public void ARecordingWithoutSpeechGivesNoWindows() => Assert.Empty(CanaryWindow.SpeechWindows(Plan(C(0, 0, 60_000, false)), 60_000)!);

    [Fact]
    public void LanguageSamplesStartOnSpeech()
    {
        // 600 s recording, speech from 120 s to 480 s only: the default positions 0 s, 292 s and 585 s would hit silence at both ends.
        var plan = Plan(C(0, 0, 119_800, false), C(1, 119_800, 200_000, true), C(2, 200_000, 300_000, true), C(3, 300_000, 480_200, true), C(4, 480_200, 600_000, false));
        var starts = LanguageSamples.From(plan, 600)!;
        Assert.Equal(3, starts.Count);
        Assert.Equal(119.8, starts[0]);
        Assert.All(starts, start => Assert.InRange(start, 119.8, 480.2));
    }

    [Fact]
    public void LanguageSamplesNeverRunPastTheEnd()
    {
        var plan = Plan(C(0, 0, 3_800, false), C(1, 3_800, 57_323, true));
        var starts = LanguageSamples.From(plan, 57.323)!;
        Assert.All(starts, start => Assert.True(start + LanguageSamples.WindowSeconds <= 57.323 + 1e-9));
        var tail = Plan(C(0, 0, 110_000, false), C(1, 110_000, 118_000, true));
        Assert.Equal([103.0], LanguageSamples.From(tail, 118)!);
    }

    [Fact]
    public void LanguageSamplesAreSkippedWhenThePlanCannotHelp()
    {
        Assert.Null(LanguageSamples.From(Plan(C(0, 0, 60_000, false)), 60));      // no speech at all
        Assert.Null(LanguageSamples.From(Mixed, 55));                              // a different recording
        Assert.Single(LanguageSamples.From(Plan(C(0, 0, 3_000, false), C(1, 3_000, 20_000, true)), 20)!); // short file: one sample
    }
}

public sealed class OverlapTextTests
{
    [Fact]
    public void WordsRepeatedAtTheSeamAreRemoved()
        => Assert.Equal("gingen wir nach Hause", OverlapText.TrimRepeatedStart("Er sagte dass wir gehen, und dann", "und dann gingen wir nach Hause"));

    [Fact]
    public void ThePartialRepeatIsRemovedToo()
        => Assert.Equal("nach Hause", OverlapText.TrimRepeatedStart("wir gingen langsam", "gingen langsam nach Hause"));

    [Fact]
    public void CaseAndPunctuationDoNotHideARepeat()
        => Assert.Equal("weiter", OverlapText.TrimRepeatedStart("Hallo, Welt!", "hallo welt weiter"));

    [Fact]
    public void TextWithoutARepeatIsLeftAlone()
        => Assert.Equal("Das ist neu", OverlapText.TrimRepeatedStart("Es war einmal", "Das ist neu"));

    [Fact]
    public void ARepeatLongerThanTheLimitIsNotGuessedAt()
        // Six words overlap, but only three may be compared: that is not enough evidence, so nothing is removed.
        => Assert.Equal("a b c d e f g", OverlapText.TrimRepeatedStart("x a b c d e f", "a b c d e f g", maximumWords: 3));

    [Fact]
    public void JoinOnlyTrimsChunksThatOverlapTheOneBefore()
    {
        // The second chunk starts with the same word as the first ends with, but it does not overlap: nothing may be removed.
        Assert.Equal("Das ist gut gut so", OverlapText.Join([("Das ist gut", false), ("gut so", false)]));
        Assert.Equal("Das ist gut so", OverlapText.Join([("Das ist gut", false), ("gut so", true)]));
    }

    [Fact]
    public void JoinSkipsEmptyChunksAndUsesTheLastChunkWithTextAsTheReference()
        => Assert.Equal("eins zwei drei", OverlapText.Join([("eins zwei", false), ("", false), ("zwei drei", true)]));
}

public sealed class WaveRangeTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N") + ".wav");
    private readonly short[] _pcm;

    public WaveRangeTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var random = new Random(7);
        // 75 s with loud and quiet stretches, so the quiet-point search has something to find.
        _pcm = new short[75 * 16000];
        for (var i = 0; i < _pcm.Length; i++)
        {
            var quiet = (i / 16000) % 7 == 3;
            _pcm[i] = (short)random.Next(quiet ? -40 : -9000, quiet ? 40 : 9000);
        }
        using var writer = new BinaryWriter(File.Create(_path));
        writer.Write("RIFF"u8); writer.Write(36 + _pcm.Length * 2); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(_pcm.Length * 2);
        foreach (var sample in _pcm) writer.Write(sample);
    }
    public void Dispose() { try { File.Delete(_path); } catch (IOException) { } }

    /// <summary>The whole-file reader as it was before ranges existed, kept here to prove the refactoring changed nothing.</summary>
    private static List<float[]> OriginalChunks(short[] pcm, int maximumSeconds)
    {
        var chunks = new List<float[]>(); var position = 0; long remaining = pcm.Length;
        while (remaining > 0)
        {
            var count = (int)Math.Min(remaining, maximumSeconds * 16000L);
            var samples = new float[count];
            for (var i = 0; i < count; i++) samples[i] = pcm[position + i] / 32768f;
            if (remaining > count && count > 16000)
            {
                var best = count; var lowest = double.MaxValue;
                for (var start = count * 3 / 4; start < count - 1600; start += 160)
                {
                    double energy = 0;
                    for (var i = start; i < start + 1600; i++) energy += samples[i] * samples[i];
                    if (energy < lowest) { lowest = energy; best = start + 800; }
                }
                Array.Resize(ref samples, best); count = best;
            }
            position += count; remaining -= count;
            chunks.Add(samples);
        }
        return chunks;
    }

    [Theory]
    [InlineData(20)]
    [InlineData(7)]
    [InlineData(60)]
    public void WholeFileChunksAreIdenticalToTheOriginalAlgorithm(int seconds)
    {
        var expected = OriginalChunks(_pcm, seconds);
        var actual = TriAsr.Audio.WaveAudio.ReadChunks(_path, seconds).ToList();
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++) Assert.Equal(expected[i], actual[i]);
    }

    [Fact]
    public void ARangeReturnsExactlyThoseSamples()
    {
        var pieces = TriAsr.Audio.WaveAudio.ReadRange(_path, 10_000, 17_500, 40).ToList();
        var piece = Assert.Single(pieces);
        Assert.Equal(7_500 * 16, piece.Length);
        Assert.Equal(_pcm[160_000] / 32768f, piece[0]);
        Assert.Equal(_pcm[160_000 + 7_500 * 16 - 1] / 32768f, piece[^1]);
    }

    [Fact]
    public void ALongRangeIsSplitWithinTheRangeAndLosesNothing()
    {
        var pieces = TriAsr.Audio.WaveAudio.ReadRange(_path, 5_000, 65_000, 20).ToList();
        Assert.All(pieces, piece => Assert.True(piece.Length <= 20 * 16000));
        Assert.True(pieces.Count >= 3);
        var joined = pieces.SelectMany(piece => piece).ToArray();
        Assert.Equal(60_000 * 16, joined.Length);
        Assert.Equal(_pcm.Skip(5_000 * 16).Take(60_000 * 16).Select(sample => sample / 32768f), joined);
    }

    [Fact]
    public void ARangePastTheEndIsClippedAndOneBeyondTheEndIsEmpty()
    {
        Assert.Equal(5 * 16000, TriAsr.Audio.WaveAudio.ReadRange(_path, 70_000, 999_000, 40).Sum(piece => piece.Length));
        Assert.Empty(TriAsr.Audio.WaveAudio.ReadRange(_path, 80_000, 90_000, 40));
        Assert.Empty(TriAsr.Audio.WaveAudio.ReadRange(_path, 5_000, 5_000, 40));
        Assert.Throws<ArgumentOutOfRangeException>(() => TriAsr.Audio.WaveAudio.ReadRange(_path, 9_000, 8_000, 40));
        Assert.Throws<ArgumentOutOfRangeException>(() => TriAsr.Audio.WaveAudio.ReadRange(_path, -1, 8_000, 40));
    }
}
