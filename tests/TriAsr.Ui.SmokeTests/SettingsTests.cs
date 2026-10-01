using System.IO;
using Microsoft.Extensions.DependencyInjection;

namespace TriAsr.Ui.SmokeTests;

public sealed class SettingsTests
{
    [Fact]
    public async Task SettingsSurviveReloadAndMalformedFileDoesNotBlockStartup()
    {
        var root = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var host = App.App.CreateHost(root);
            var store = host.Services.GetRequiredService<App.SettingsStore>();
            Assert.Equal("System", (await store.LoadAsync()).Theme);
            await store.SaveAsync(new("Dark", "Compact"));
            Assert.Equal(new App.AppSettings("Dark", "Compact"), await store.LoadAsync());
            await store.SaveAsync(new("Dark", "Compact", AnimateErrors: false));
            Assert.False((await store.LoadAsync()).AnimateErrors);
            await store.SaveAsync(new("Light", "Comfortable"));
            Assert.True(File.Exists(store.FilePath + ".bak"));
            await File.WriteAllTextAsync(store.FilePath, "{invalid-json");
            Assert.Equal("System", (await store.LoadAsync()).Theme);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
