using System.Buffers.Binary;
using System.IO;
using TriAsr.Application;
using TriAsr.Domain;

namespace TriAsr.Audio.Tests;

/// <summary>A recording in two languages (<see cref="LanguageBlocks"/>): which language each speech stretch is in, the blocks of one language, and Canary's windows.</summary>
public sealed class LanguageBlockTests
{
    // 60 s: quiet, Hungarian, quiet, English, English (cut in the middle of speech, so it overlaps), quiet.
    private static readonly ChunkPlan Plan = new(ChunkPlan.CurrentVersion, 60_000, "test",
    [
        new(0, 0, 4_000, false, false), new(1, 4_000, 20_000, true, false), new(2, 20_000, 26_000, false, false),
        new(3, 26_000, 46_000, true, false), new(4, 45_000, 56_000, true, true), new(5, 56_000, 60_000, false, false)
    ]);

    private static readonly IReadOnlyList<string> Pair = ["en", "hu"];

    private static SpeechReading Read(string language, double confidence, string text = "words") => new(language, 1, confidence, text);

    [Fact]
    public void AStretchTheProgramIsSureOfIsTakenAsDetectedAndTheOthersByTheReadings()
    {
        // The measured case: the Hungarian stretch was detected at 0.998; the English ones were heard as Hungarian at 0.97 and 0.89, and read far better in English.
        ChunkDetection[] detections = [new(1, "hu", 0.998), new(3, "hu", 0.97), new(4, "hu", 0.888)];
        var readings = new Dictionary<int, IReadOnlyList<SpeechReading>>
        {
            [3] = [Read("en", -0.068), Read("hu", -0.75)],
            [4] = [Read("en", -0.143), Read("hu", -0.926)]
        };
        Assert.Equal(new Dictionary<int, string> { [1] = "hu", [3] = "en", [4] = "en" }, LanguageBlocks.Decide(detections, readings, Pair));
    }

    [Fact]
    public void ACloseCallGoesToThePreviousStretchAndAStretchWithoutWordsFollowsIt()
    {
        ChunkDetection[] detections = [new(1, "en", 0.995), new(3, "hu", 0.84), new(4, "zh", 0.9)];
        var readings = new Dictionary<int, IReadOnlyList<SpeechReading>>
        {
            [3] = [Read("en", -0.165), Read("hu", -0.104)],                              // Hungarian reads a little better, but not by the margin
            [4] = [Read("en", -99, ""), Read("hu", -0.2, "Feliratok az Amara.org közösségétől")]
        };
        Assert.Equal(new Dictionary<int, string> { [1] = "en", [3] = "en", [4] = "en" }, LanguageBlocks.Decide(detections, readings, Pair));
    }

    [Fact]
    public void QuietStretchesGoToTheirNeighboursAndASwitchIsMadeInTheMiddleOfThePause()
    {
        var blocks = LanguageBlocks.From(Plan, new Dictionary<int, string> { [1] = "hu", [3] = "en", [4] = "en" }, "en");
        Assert.Equal([new LanguageBlock(0, 23_000, "hu"), new LanguageBlock(23_000, 60_000, "en")], blocks);
        Assert.Equal(["en", "hu"], LanguageBlocks.Present(blocks, Pair));
    }

    [Fact]
    public void OverlappingSpeechInTwoLanguagesIsNotReadTwice()
    {
        var blocks = LanguageBlocks.From(Plan, new Dictionary<int, string> { [1] = "hu", [3] = "hu", [4] = "en" }, "en");
        Assert.Equal([new LanguageBlock(0, 46_000, "hu"), new LanguageBlock(46_000, 60_000, "en")], blocks);
        Assert.Equal(60_000, blocks.Sum(block => block.DurationMs));                       // the blocks cover the recording once, end to end
    }

    [Fact]
    public void ARecordingInOneLanguageIsOneBlockAndOneWithoutSpeechIsInTheFallback()
    {
        Assert.Equal([new LanguageBlock(0, 60_000, "hu")], LanguageBlocks.From(Plan, new Dictionary<int, string> { [1] = "hu", [3] = "hu", [4] = "hu" }, "en"));
        Assert.Equal(["hu"], LanguageBlocks.Present([new LanguageBlock(0, 60_000, "hu")], Pair));
        var silent = new ChunkPlan(ChunkPlan.CurrentVersion, 30_000, "test", [new(0, 0, 30_000, false, false)]);
        Assert.Equal([new LanguageBlock(0, 30_000, "en")], LanguageBlocks.From(silent, new Dictionary<int, string>(), "en"));
    }

    [Fact]
    public void CanaryReadsTheSpeechOfEachLanguageInItsOwnWindowsCutAtTheBlockEdges()
    {
        LanguageBlock[] blocks = [new(0, 30_000, "hu"), new(30_000, 60_000, "en")];
        var hungarian = LanguageBlocks.Windows(Plan, blocks, "hu");
        var english = LanguageBlocks.Windows(Plan, blocks, "en");
        Assert.Equal([(1, 4_000L, 20_000L), (3, 26_000L, 30_000L)], hungarian.Select(window => (window.Index, window.StartMs, window.EndMs)));
        Assert.Equal([(3, 30_000L, 46_000L), (4, 45_000L, 56_000L)], english.Select(window => (window.Index, window.StartMs, window.EndMs)));
        Assert.False(english[0].OverlapsPrevious);
        Assert.True(english[1].OverlapsPrevious);                                            // the repeated words at a cut in speech are still trimmed
        Assert.All(hungarian.Concat(english), window => Assert.True(window.IsSpeech));
    }

    [Fact]
    public void WithoutTheSpeechDetectorTheRecordingIsCutIntoEvenStretches()
    {
        var plan = LanguageBlocks.Even(50_000);
        Assert.Equal("even", plan.Source);
        Assert.All(plan.SpeechChunks(), chunk => Assert.InRange(chunk.DurationMs, 1, ChunkOptions.ForCanary.HardCapMs));
        Assert.Equal(50_000, plan.Chunks[^1].EndMs);
    }

    // ---- cutting the recording ----------------------------------------------------------------------------------------------------

    private static string WriteRamp(int seconds, bool extraChunk = false)
    {
        var path = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N") + ".wav");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var samples = seconds * 16_000;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8); writer.Write(0); writer.Write("WAVE"u8);
        writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
        if (extraChunk) { writer.Write("LIST"u8); writer.Write(5); writer.Write(new byte[6]); }          // an odd-sized chunk before the sound, as FFmpeg writes
        writer.Write("data"u8); writer.Write(samples * 2);
        for (var i = 0; i < samples; i++) writer.Write((short)(i / 16));                                   // the sample says which millisecond it is in
        return path;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AStretchIsCutOutAsAWavFileOfItsOwnAtTheRightPlace(bool extraChunk)
    {
        var path = WriteRamp(3, extraChunk);
        try
        {
            var wav = WaveSlice.Cut(path, 1_000, 1_250);
            Assert.Equal(44 + 250 * 32, wav.Length);
            Assert.Equal(1_000, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44)));
            Assert.Equal(1_249, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(wav.Length - 2)));
            Assert.Equal(44 + 500 * 32, WaveSlice.Cut(path, 2_500, 9_000).Length);                    // past the end: what there is
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SoundThatIsNotTheNormalizedFormatIsRefused()
    {
        var path = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N") + ".wav");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write("RIFF"u8); writer.Write(0); writer.Write("WAVE"u8);
            writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)2); writer.Write(44100); writer.Write(176400); writer.Write((short)4); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(8); writer.Write(new byte[8]);
        }
        try { Assert.Throws<InvalidDataException>(() => WaveSlice.Cut(path, 0, 10)); }
        finally { File.Delete(path); }
    }
}
