using System.IO;
using TriAsr.Application;
using TriAsr.Engine.Canary;

namespace TriAsr.Engine.Tests;

public sealed class CanaryWindowRunnerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_directory, true); } catch (IOException) { } }

    private static CanaryWindow W(int index, long start, long end, bool speech = true, bool overlap = false) => new(index, start, end, speech, overlap);
    private static readonly IReadOnlyList<CanaryWindow> Four = [W(0, 0, 10_000), W(1, 10_000, 30_000), W(2, 30_000, 40_000, speech: false), W(4, 50_000, 60_000)];

    /// <summary>The recogniser is told which window a piece belongs to through its length (index + 1 samples).</summary>
    private static IEnumerable<float[]> OnePiece(CanaryWindow window) => [new float[window.Index + 1]];
    private static (string, string) Say(float[] samples) => ($"text{samples.Length - 1}", $"raw{samples.Length - 1}");

    [Fact]
    public void EveryWindowIsRecognisedInOrderAndProgressReachesOne()
    {
        var progress = new List<double>();
        var results = CanaryWindowRunner.Run(Four, null, "k", OnePiece, Say, progress.Add);
        Assert.Equal(["text0", "text1", "text2", "text4"], results.Select(result => result.Text));
        Assert.Equal([0, 10_000, 30_000, 50_000], results.Select(result => result.StartMs));
        Assert.Equal(progress.OrderBy(value => value), progress);
        Assert.Equal(1.0, progress[^1], 6);
    }

    [Fact]
    public void ARestartContinuesAfterTheLastFinishedWindow()
    {
        var calls = new List<int>();
        (string, string) Failing(float[] samples) { var index = samples.Length - 1; calls.Add(index); if (index == 2) throw new InvalidOperationException("crash"); return Say(samples); }
        Assert.Throws<InvalidOperationException>(() => CanaryWindowRunner.Run(Four, _directory, "k", OnePiece, Failing));
        Assert.Equal([0, 1, 2], calls);
        Assert.Equal(2, Directory.GetFiles(_directory, "*.json").Length);

        calls.Clear();
        (string, string) Counting(float[] samples) { calls.Add(samples.Length - 1); return Say(samples); }
        var results = CanaryWindowRunner.Run(Four, _directory, "k", OnePiece, Counting);
        Assert.Equal([2, 4], calls);                       // windows 0 and 1 were not recognised again
        Assert.Equal(["text0", "text1", "text2", "text4"], results.Select(result => result.Text));
    }

    [Fact]
    public void ASavedWindowFromAnotherModelOrLanguageIsNotReused()
    {
        CanaryWindowRunner.Run(Four, _directory, "model-a|de", OnePiece, Say);
        var calls = 0;
        CanaryWindowRunner.Run(Four, _directory, "model-a|hu", OnePiece, samples => { calls++; return Say(samples); });
        Assert.Equal(4, calls);
    }

    [Fact]
    public void ASavedWindowThatNoLongerMatchesTheWindowIsRecognisedAgain()
    {
        CanaryWindowRunner.Run(Four, _directory, "k", OnePiece, Say);
        var moved = Four.Select(window => window.Index == 1 ? window with { EndMs = 29_000 } : window).ToList();
        var calls = new List<int>();
        CanaryWindowRunner.Run(moved, _directory, "k", OnePiece, samples => { calls.Add(samples.Length - 1); return Say(samples); });
        Assert.Equal([1], calls);
    }

    [Fact]
    public void ADamagedSavedWindowIsRecognisedAgainInsteadOfFailingTheRun()
    {
        CanaryWindowRunner.Run(Four, _directory, "k", OnePiece, Say);
        File.WriteAllText(Path.Combine(_directory, "000001.json"), "{ not json");
        var calls = new List<int>();
        var results = CanaryWindowRunner.Run(Four, _directory, "k", OnePiece, samples => { calls.Add(samples.Length - 1); return Say(samples); });
        Assert.Equal([1], calls);
        Assert.Equal("text1", results[1].Text);
    }

    [Fact]
    public void AWindowReadInSeveralPiecesJoinsTheirTextAndIgnoresEmptyOnes()
    {
        IEnumerable<float[]> Pieces(CanaryWindow window) => [new float[1], new float[2], new float[3]];
        var results = CanaryWindowRunner.Run([W(0, 0, 60_000, speech: false)], null, "k", Pieces, samples => (samples.Length == 2 ? "  " : $"part{samples.Length}", $"r{samples.Length}"));
        Assert.Equal("part1 part3", Assert.Single(results).Text);
        Assert.Equal("r1 r2 r3", results[0].Raw);
    }

    [Fact]
    public void NoWindowsGiveNoResults() => Assert.Empty(CanaryWindowRunner.Run([], null, "k", OnePiece, Say));

    [Fact]
    public void AssemblingRemovesTheRepeatAtAnOverlapAndTimesEveryWindowThatHasText()
    {
        var results = new[]
        {
            new WindowResult(0, 0, 30_000, false, "wir gingen nach Hause und dann", "r"),
            new WindowResult(1, 29_000, 50_000, true, "und dann trafen wir Peter", "r"),
            new WindowResult(2, 50_000, 60_000, false, "", "r"),
            new WindowResult(3, 60_000, 70_000, false, "Ende", "r"),
        };
        var (text, segments) = CanaryWindowRunner.Assemble(results);
        Assert.Equal("wir gingen nach Hause und dann trafen wir Peter Ende", text);
        Assert.Equal([(0L, 30_000L, "wir gingen nach Hause und dann"), (29_000L, 50_000L, "trafen wir Peter"), (60_000L, 70_000L, "Ende")],
            segments.Select(segment => (segment.StartMs, segment.EndMs, segment.Text)));
    }
}
