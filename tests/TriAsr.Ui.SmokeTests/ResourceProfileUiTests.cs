using System.IO;
using Microsoft.Extensions.DependencyInjection;
using TriAsr.Hardware;

namespace TriAsr.Ui.SmokeTests;

public sealed class ResourceProfileUiTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline) { if (await condition()) return; await Task.Delay(50); }
        throw new TimeoutException("The expected state was not reached.");
    }

    [Fact]
    public async Task TheChoiceReachesTheGovernorIsSavedAndSurvivesARestart()
    {
        var root = NewRoot();
        try
        {
            using (var host = App.App.CreateHost(root))
            {
                var shell = host.Services.GetRequiredService<App.ShellViewModel>();
                var governor = host.Services.GetRequiredService<ResourceGovernor>();
                await shell.InitializeAsync();
                Assert.Equal("Auto", shell.SelectedResourceProfile);
                Assert.Equal(ResourceProfile.Auto, governor.Profile);
                shell.SelectedResourceProfile = "Quiet";
                Assert.Equal(ResourceProfile.Quiet, governor.Profile);
                Assert.Contains("Quiet", shell.ResourceSummary);
                var store = host.Services.GetRequiredService<App.SettingsStore>();
                await WaitForAsync(async () => (await store.LoadAsync()).ResourceProfile == "Quiet");
            }
            using (var host = App.App.CreateHost(root))
            {
                var shell = host.Services.GetRequiredService<App.ShellViewModel>();
                await shell.InitializeAsync();
                Assert.Equal("Quiet", shell.SelectedResourceProfile);
                Assert.Equal(ResourceProfile.Quiet, host.Services.GetRequiredService<ResourceGovernor>().Profile);
            }
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task AnUnknownSavedValueFallsBackToAuto()
    {
        var root = NewRoot();
        try
        {
            using var host = App.App.CreateHost(root);
            var store = host.Services.GetRequiredService<App.SettingsStore>();
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            await File.WriteAllTextAsync(store.FilePath, "{\"theme\":\"Dark\",\"resourceProfile\":\"Turbo\"}");
            Assert.Equal("Auto", (await store.LoadAsync()).ResourceProfile);
            var shell = host.Services.GetRequiredService<App.ShellViewModel>();
            await shell.InitializeAsync();
            Assert.Equal("Auto", shell.SelectedResourceProfile);
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task SettingsSavedByEarlierVersionsDefaultToAuto()
    {
        var root = NewRoot();
        try
        {
            using var host = App.App.CreateHost(root);
            var store = host.Services.GetRequiredService<App.SettingsStore>();
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            await File.WriteAllTextAsync(store.FilePath, "{\"theme\":\"System\",\"density\":\"Comfortable\",\"version\":1,\"animateErrors\":true}");
            Assert.Equal("Auto", (await store.LoadAsync()).ResourceProfile);
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public void EveryServiceThatNowNeedsTheGovernorStillResolves()
    {
        var root = NewRoot();
        try
        {
            using var host = App.App.CreateHost(root);
            Assert.NotNull(host.Services.GetRequiredService<App.LocalTranscriptionStages>());
            Assert.NotNull(host.Services.GetRequiredService<App.LocalOptimizer>());
            Assert.NotNull(host.Services.GetRequiredService<App.ShellViewModel>());
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task OtherPreferenceChangesKeepTheResourceChoice()
    {
        var root = NewRoot();
        try
        {
            using var host = App.App.CreateHost(root);
            var shell = host.Services.GetRequiredService<App.ShellViewModel>();
            var store = host.Services.GetRequiredService<App.SettingsStore>();
            await shell.InitializeAsync();
            shell.SelectedResourceProfile = "Max";
            shell.SelectedDensity = "Compact";
            try { await WaitForAsync(async () => { var s = await store.LoadAsync(); return s.Density == "Compact" && s.ResourceProfile == "Max"; }); }
            catch (TimeoutException) { throw new InvalidOperationException("settings.json was: " + (File.Exists(store.FilePath) ? await File.ReadAllTextAsync(store.FilePath) : "<missing>") + " | status: " + shell.Status + " | error: " + shell.ErrorMessage); }
        }
        finally { TestCleanup.Delete(root); }
    }
}
