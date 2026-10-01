using TriAsr.Hardware;
using TriAsr.Benchmark;
using TriAsr.App;
namespace TriAsr.Ui.SmokeTests;
public sealed class SetupExperienceTests
{
    private static HardwareProfile Hardware(ulong ram, ulong vram, bool vulkan) => new("test", "test", new(8, 16, 8, 0), ram * 1073741824,
        true, true, true, true, [new("GPU", 0, vram * 1073741824, "test")], vulkan ? ["GPU"] : [], "unavailable", [], "Windows", 100L * 1073741824, "fixture");
    [Theory]
    [InlineData(64UL, 20UL, true, "whisper-large-v3", "canary-q8", "correction-q6")]
    [InlineData(16UL, 8UL, true, "whisper-large-v3-q5", "canary-q8", "correction-q4")]
    [InlineData(64UL, 20UL, false, "whisper-large-v3-q5", "canary-q4", "correction-q4")]
    [InlineData(8UL, 2UL, true, "whisper-large-v3-q5", "canary-q4", "correction-q4")]
    public void RecommendationsRequireUsableGpuAndEnoughMemory(ulong ram, ulong vram, bool vulkan, string whisper, string canary, string correction)
    {
        var choice = ModelRecommendation.For(Hardware(ram, vram, vulkan));
        Assert.Equal(whisper, choice.Whisper); Assert.Equal(canary, choice.Canary); Assert.Equal(correction, choice.Correction);
    }
    [Fact]
    public void CorrectionLatencyDoesNotClaimAnAudioSpeedAndFailedCandidatesAreExcluded()
    {
        Measurement[] runs = [new(4), new(5), new(3)];
        var speech = new BenchmarkRow("Whisper", "fixture", "vulkan", 8, "single", 8, runs);
        Assert.Equal(.5, speech.RealTimeFactor); Assert.Contains("real time", speech.Meaning);
        var correction = speech with { Engine = "Correction" };
        Assert.Null(correction.RealTimeFactor); Assert.Contains("per disagreement", correction.Meaning);
        var failed = speech with { Error = "failed" };
        Assert.Null(failed.MedianSeconds); Assert.Contains("excluded", failed.Meaning);
    }
    [Fact]
    public async Task TuningExcludesFailuresAndPreservesCancellation()
    {
        var failed = await BenchmarkSession.MeasureAsync("Whisper", "test", "vulkan", 8, "single", 8,
            (_, _) => throw new InvalidOperationException("GPU unavailable"), CancellationToken.None);
        Assert.Equal("GPU unavailable", failed.Error); Assert.Null(failed.MedianSeconds);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BenchmarkSession.MeasureAsync("Whisper", "test", "cpu", 4, "single", 8,
            (_, token) => { token.ThrowIfCancellationRequested(); return Task.FromResult(new Measurement(1)); }, cancellation.Token));
    }
}
