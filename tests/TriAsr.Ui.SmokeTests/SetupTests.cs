using System.IO;
using Microsoft.Extensions.DependencyInjection;
using TriAsr.App;
using TriAsr.Audio;
using TriAsr.Infrastructure;

namespace TriAsr.Ui.SmokeTests;

public sealed class SetupTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("pending", true, false, true)]
    [InlineData("pending", true, true, true)]
    [InlineData("pending", false, false, true)]
    [InlineData("pending", false, true, false)]
    [InlineData("done", true, false, false)]
    [InlineData("skipped", true, false, false)]
    public void SetupIsOfferedUntilTheUserAnswersAndOnlyWhileThereIsSomethingToDo(string state, bool modelsMissing, bool tuned, bool offered)
        => Assert.Equal(offered, SetupPlan.ShouldOffer(state, modelsMissing, tuned));

    [Theory]
    [InlineData(null, "pending")]
    [InlineData("", "pending")]
    [InlineData("done", "done")]
    [InlineData("skipped", "skipped")]
    [InlineData("whatever", "pending")]
    public void AnUnknownSavedStateMeansTheUserHasNotAnswered(string? saved, string expected) => Assert.Equal(expected, SetupPlan.Normalize(saved));

    [Fact]
    public void DownloadProgressCountsFinishedAndPartialFiles()
    {
        Assert.Equal(1, SetupPlan.DownloadFraction([]));
        Assert.Equal(0, SetupPlan.DownloadFraction([(100, 0), (300, 0)]));
        Assert.Equal(0.25, SetupPlan.DownloadFraction([(100, 100), (300, 0)]));
        Assert.Equal(0.5, SetupPlan.DownloadFraction([(100, 100), (300, 100)]), 3);
        Assert.Equal(1, SetupPlan.DownloadFraction([(100, 500), (300, 300)])); // a file larger than expected never pushes the bar past full
    }

    [Fact]
    public void TheTestSpeechScriptCoversTheFiveLanguagesAndSurvivesQuotesInThePath()
    {
        Assert.Equal(["en", "hu", "de", "es", "fr"], TestSpeech.Languages);
        var script = TestSpeech.Script(@"C:\Users\O'Brien\setup\test-speech.wav");
        foreach (var code in TestSpeech.Languages) Assert.Contains($"{code} = '", script);
        Assert.Contains("'C:\\Users\\O''Brien\\setup\\test-speech.wav'", script);
        Assert.Contains($"exit {TestSpeech.NoVoice}", script);
        Assert.Contains("számítógép", script); // the Hungarian sentence keeps its accents
        Assert.Contains("qu''une courte", script); // an apostrophe in a sentence is escaped, not allowed to end the string
    }

    [Fact]
    public async Task WindowsMakesASpeechRecordingTheEnginesCanRead()
    {
        var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(shell)) return; // nothing to run on a machine without Windows PowerShell
        var root = NewRoot();
        try
        {
            var path = await TestSpeech.CreateAsync(new ProcessRunner(), root, CancellationToken.None);
            if (path is null) return; // this computer has no voice for the five languages; setup skips tuning then
            var info = WaveAudio.Inspect(path);
            Assert.Equal(16000, info.SampleRate);
            Assert.Equal(1, info.Channels);
            Assert.InRange(info.DurationSeconds, 5, 60); // tuning needs at least three seconds and uses up to eight
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task TheOfferAppearsWhileSomethingIsMissingAndNotNowIsRemembered()
    {
        var root = NewRoot();
        try
        {
            using (var host = App.App.CreateHost(root))
            {
                var shell = host.Services.GetRequiredService<ShellViewModel>();
                await shell.InitializeAsync();
                shell.EvaluateSetupOffer(requiredModelsMissing: true, tuned: false);
                Assert.Equal(SetupStage.Offer, shell.SetupPhase);
                Assert.True(shell.SetupCanStart);
                Assert.Equal("Not now", shell.SetupDismissLabel);
                Assert.Contains("Downloads the speech models", shell.SetupDetail);
                shell.EvaluateSetupOffer(requiredModelsMissing: false, tuned: true);
                Assert.Equal(SetupStage.Hidden, shell.SetupPhase); // everything was done by hand meanwhile
                shell.EvaluateSetupOffer(requiredModelsMissing: true, tuned: false);
                shell.DismissSetupCommand.Execute(null);
                Assert.Equal(SetupStage.Hidden, shell.SetupPhase);
                var store = host.Services.GetRequiredService<SettingsStore>();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while ((await store.LoadAsync()).SetupState != SetupPlan.Skipped && DateTime.UtcNow < deadline) await Task.Delay(50);
                Assert.Equal(SetupPlan.Skipped, (await store.LoadAsync()).SetupState);
            }
            using (var host = App.App.CreateHost(root))
            {
                var shell = host.Services.GetRequiredService<ShellViewModel>();
                await shell.InitializeAsync();
                shell.EvaluateSetupOffer(requiredModelsMissing: true, tuned: false);
                Assert.Equal(SetupStage.Hidden, shell.SetupPhase); // the answer survives a restart
            }
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task ASavedAnswerOfDoneKeepsTheOfferAway()
    {
        var root = NewRoot();
        try
        {
            using var host = App.App.CreateHost(root);
            var store = host.Services.GetRequiredService<SettingsStore>();
            await store.SaveAsync(new(SetupState: SetupPlan.Done));
            var shell = host.Services.GetRequiredService<ShellViewModel>();
            await shell.InitializeAsync();
            shell.EvaluateSetupOffer(requiredModelsMissing: true, tuned: false);
            Assert.Equal(SetupStage.Hidden, shell.SetupPhase);
        }
        finally { TestCleanup.Delete(root); }
    }
}
