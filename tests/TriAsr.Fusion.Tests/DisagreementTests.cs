using TriAsr.Domain;
using TriAsr.Fusion;

namespace TriAsr.Fusion.Tests;

public sealed class DisagreementTests
{
    [Fact]
    public void OnlyLexicalDifferencesAreDisputesAndCanaryTimesAreProjected()
    {
        var whisper = new EngineTranscript("Whisper", "fixture", "fixture", "cpu", "cpu", "CPU", "de", 10, 1,
            [new(0, 10000, "Training dauert 90 Minuten. Basketball spielen.")], "", true);
        var canary = whisper with { Engine = "Canary", Segments = [], Text = "Training dauert neunzig Minuten. Basketballspielen.", NativeTimestamps = false };
        var comparison = DisagreementDetector.Compare(whisper, canary);
        Assert.Empty(comparison.Disagreements);
        Assert.Equal(10000, comparison.Regions[0].EndMs);
        Assert.Equal("agreement", comparison.Regions[0].Source);
    }
    [Fact]
    public void CrossRegionDisputesNeverDuplicateCanaryWords()
    {
        var whisper = new EngineTranscript("Whisper", "fixture", "fixture", "cpu", "cpu", "CPU", "en", 2, 1,
            [new(0, 1000, "hello red"), new(1000, 2000, "blue end")], "", true);
        var comparison = DisagreementDetector.Compare(whisper, whisper with { Text = "hello green yellow end", Segments = [], NativeTimestamps = false });
        Assert.Equal(2, comparison.Disagreements.Count);
        Assert.Equal(new[] { "green", "yellow" }, comparison.Disagreements.Select(item => item.Canary));
    }
    [Fact]
    public void OneSidedInsertionUsesItsAlignmentAnchor()
    {
        var whisper = new EngineTranscript("Whisper", "fixture", "fixture", "cpu", "cpu", "CPU", "en", 2, 1,
            [new(0, 2000, "hello end")], "", true);
        var comparison = DisagreementDetector.Compare(whisper, whisper with { Text = "hello extra end", Segments = [], NativeTimestamps = false });
        var dispute = Assert.Single(comparison.Disagreements);
        Assert.Equal(5, dispute.StartCharacter); Assert.Equal(0, dispute.Length); Assert.Equal("extra", dispute.Canary);
    }
}
