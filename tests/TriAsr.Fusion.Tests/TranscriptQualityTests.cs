using TriAsr.Domain;
using TriAsr.Fusion;

namespace TriAsr.Fusion.Tests;
public sealed class TranscriptQualityTests
{
    private static FinalRegion Region(int index, string text) => new(index * 1000, (index + 1) * 1000, text, text, text, "agreement");
    [Fact]
    public void AlternatingRecognitionLoopIsFlaggedWithoutChangingWordsTimingOrProvenance()
    {
        var regions = Enumerable.Range(0, 24).Select(index => Region(index, index % 2 == 0 ? "First example phrase here" : "Second example phrase here")).ToArray();
        var input = new FinalTranscript(Guid.NewGuid(), "en", regions);
        var result = TranscriptQuality.FlagRepetition(input);
        Assert.All(result.Regions, region => Assert.Contains(TranscriptQuality.RepetitionWarning, region.Warnings!));
        for (var index = 0; index < regions.Length; index++)
            Assert.Equal(regions[index], result.Regions[index] with { Warnings = null });
        Assert.All(input.Regions, region => Assert.Null(region.Warnings));
        Assert.All(TranscriptQuality.FlagRepetition(result).Regions, region => Assert.Single(region.Warnings!));
    }
    [Fact]
    public void NormalChorusRepetitionsAndShortSpokenHesitationArePreservedWithoutWarning()
    {
        var chorus = Enumerable.Range(0, 3).SelectMany(copy => new[] { "First example phrase here", "Second example phrase here", "A different verse between choruses " + copy }).Select((text, index) => Region(index, text));
        var hesitation = Enumerable.Range(0, 30).Select(index => Region(index, "yes"));
        foreach (var regions in new[] { chorus, hesitation })
            Assert.All(TranscriptQuality.FlagRepetition(new(Guid.NewGuid(), "en", regions.ToArray())).Regions, region => Assert.Null(region.Warnings));
    }
    [Fact]
    public void SeparateRepeatedSectionsWithLongGapsDoNotBecomeOneLoop()
    {
        var regions = Enumerable.Range(0, 12).Select(index => Region(index, "Same repeated phrase here") with { StartMs = index * 20000, EndMs = index * 20000 + 1000 }).ToArray();
        Assert.All(TranscriptQuality.FlagRepetition(new(Guid.NewGuid(), "en", regions)).Regions, region => Assert.Null(region.Warnings));
    }
}
