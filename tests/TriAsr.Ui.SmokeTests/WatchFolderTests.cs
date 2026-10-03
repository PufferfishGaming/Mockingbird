using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TriAsr.App;

namespace TriAsr.Ui.SmokeTests;

public sealed class WatchFolderTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));

    private static async Task WaitForAsync(Func<Task<bool>> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline) { if (await condition()) return; await Task.Delay(50); }
        throw new TimeoutException("Not reached: " + because);
    }

    [Fact]
    public async Task ASavedWatchFolderComesBackAndStartsWatchingWithoutTranscribingWhatIsThere()
    {
        var root = NewRoot(); var watched = Path.Combine(root, "inbox");
        Directory.CreateDirectory(watched);
        await File.WriteAllBytesAsync(Path.Combine(watched, "already-here.wav"), new byte[4096]);
        try
        {
            using var host = App.App.CreateHost(root);
            await host.Services.GetRequiredService<SettingsStore>().SaveAsync(new(WatchFolder: watched, WatchEnabled: true, WatchLanguage: "de", WatchOutput: ShellViewModel.WatchAsSubtitles));
            var shell = host.Services.GetRequiredService<ShellViewModel>();
            await shell.InitializeAsync();
            Assert.Equal(watched, shell.WatchFolder);
            Assert.True(shell.WatchEnabled);
            Assert.Equal("de", shell.WatchLanguage);
            Assert.Equal(ShellViewModel.WatchAsSubtitles, shell.WatchOutput);
            await WaitForAsync(() => Task.FromResult(shell.WatchStatus.StartsWith("Watching")), "the watcher starts");
            Assert.Empty(shell.Jobs); // the recording that was already there is left alone
            var ledger = await File.ReadAllTextAsync(Path.Combine(root, "Config", "watch-ledger.json"));
            Assert.Contains("already-here.wav", ledger, StringComparison.OrdinalIgnoreCase);
            shell.WatchEnabled = false;
            await WaitForAsync(() => Task.FromResult(shell.WatchStatus == "Off"), "switching it off stops the watcher");
            await WaitForAsync(() => Task.FromResult(shell.Status == "Preferences saved locally"), "the background save finishes before the folder is removed");
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task ChoosingAFolderSwitchesWatchingOnAndIsRemembered()
    {
        var root = NewRoot(); var watched = Path.Combine(root, "inbox");
        Directory.CreateDirectory(watched);
        try
        {
            using var host = App.App.CreateHost(root);
            var shell = host.Services.GetRequiredService<ShellViewModel>();
            await shell.InitializeAsync();
            Assert.False(shell.WatchEnabled);
            Assert.False(shell.HasWatchFolder);
            Assert.Equal("Off", shell.WatchStatus);
            shell.SetWatchFolder(watched);
            Assert.True(shell.WatchEnabled);
            Assert.True(shell.HasWatchFolder);
            var store = host.Services.GetRequiredService<SettingsStore>();
            await WaitForAsync(async () => (await store.LoadAsync()) is { WatchEnabled: true } saved && saved.WatchFolder == Path.GetFullPath(watched), "the choice is saved");
            await WaitForAsync(() => Task.FromResult(shell.WatchStatus.StartsWith("Watching")), "the watcher starts");
            await WaitForAsync(() => Task.FromResult(shell.Status == "Preferences saved locally"), "the background save finishes before the folder is removed");
            shell.StopWatchingForExit();
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task AFolderThatIsGoneIsReportedNotWatched()
    {
        var root = NewRoot();
        try
        {
            using var host = App.App.CreateHost(root);
            await host.Services.GetRequiredService<SettingsStore>().SaveAsync(new(WatchFolder: Path.Combine(root, "gone"), WatchEnabled: true));
            var shell = host.Services.GetRequiredService<ShellViewModel>();
            await shell.InitializeAsync();
            await WaitForAsync(() => Task.FromResult(shell.WatchStatus.Contains("not found")), "the missing folder is reported");
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task TwoLanguagesAreRememberedForTheWatchFolderAndSentWithANewRecording()
    {
        var root = NewRoot();
        try
        {
            using var host = App.App.CreateHost(root);
            var store = host.Services.GetRequiredService<SettingsStore>();
            await store.SaveAsync(new(WatchLanguage: "en+hu"));
            var shell = host.Services.GetRequiredService<ShellViewModel>();
            await shell.InitializeAsync();
            Assert.Equal(("en", "hu", "en+hu"), (shell.WatchLanguage, shell.WatchSecondLanguage, shell.WatchLanguageChoice));
            Assert.True(shell.CanChooseWatchSecondLanguage);
            Assert.DoesNotContain(shell.WatchSecondLanguages, language => language.Code == "en");
            shell.WatchSecondLanguage = "de";
            await WaitForAsync(async () => (await store.LoadAsync()).WatchLanguage == "en+de", "the new pair is saved");
            shell.WatchLanguage = "auto";                                                         // auto-detect takes no second language
            Assert.Equal(("", "auto"), (shell.WatchSecondLanguage, shell.WatchLanguageChoice));
            await WaitForAsync(async () => (await store.LoadAsync()).WatchLanguage == "auto", "auto-detect is saved");

            // The New transcription page offers the same choice for the recording it sends.
            Assert.False(shell.CanChooseSecondLanguage);
            shell.SelectedLanguage = "hu";
            shell.SelectedSecondLanguage = "en";
            Assert.Equal("hu+en", shell.LanguageChoice);
            shell.SelectedLanguage = "en";                                                        // the same language twice is one
            Assert.Equal(("", "en"), (shell.SelectedSecondLanguage, shell.LanguageChoice));
            await WaitForAsync(() => Task.FromResult(shell.Status == "Preferences saved locally"), "the background save finishes before the folder is removed");
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task TheSpeakersToTellApartInWatchedRecordingsAreRemembered()
    {
        var root = NewRoot();
        try
        {
            using var host = App.App.CreateHost(root);
            var store = host.Services.GetRequiredService<SettingsStore>();
            await store.SaveAsync(new(WatchSpeakers: "3"));
            var shell = host.Services.GetRequiredService<ShellViewModel>();
            await shell.InitializeAsync();
            Assert.Equal("3", shell.WatchSpeakers);
            Assert.Contains(shell.SpeakerChoices, choice => choice.Value == "auto");                // the same choices as for a recording sent by hand
            shell.WatchSpeakers = "auto";
            await WaitForAsync(async () => (await store.LoadAsync()).WatchSpeakers == "auto", "the new choice is saved");
            shell.WatchSpeakers = "off";
            await WaitForAsync(async () => (await store.LoadAsync()).WatchSpeakers == "off", "switching it off is saved");
            await WaitForAsync(() => Task.FromResult(shell.Status == "Preferences saved locally"), "the background save finishes before the folder is removed");

            // A settings file from before the choice, or with a choice that is not one, means off.
            await File.WriteAllTextAsync(store.FilePath, JsonSerializer.Serialize(new { watchSpeakers = "lots" }));
            Assert.Equal("off", (await store.LoadAsync()).WatchSpeakers);
            await File.WriteAllTextAsync(store.FilePath, "{}");
            Assert.Equal("off", (await store.LoadAsync()).WatchSpeakers);
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task AnUnknownSavedOutputFallsBackToText()
    {
        var root = NewRoot();
        try
        {
            using var host = App.App.CreateHost(root);
            var store = host.Services.GetRequiredService<SettingsStore>();
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            await File.WriteAllTextAsync(store.FilePath, JsonSerializer.Serialize(new { watchOutput = "Carrier pigeon", watchLanguage = "" }));
            var loaded = await store.LoadAsync();
            Assert.Equal("Text (.txt)", loaded.WatchOutput);
            Assert.Equal("auto", loaded.WatchLanguage);
        }
        finally { TestCleanup.Delete(root); }
    }
}
