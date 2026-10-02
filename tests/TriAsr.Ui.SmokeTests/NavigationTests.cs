using System.IO;
using Microsoft.Extensions.DependencyInjection;

namespace TriAsr.Ui.SmokeTests;

public sealed class NavigationTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TheMainPagesComeFirstAndTheRestIsAdvanced()
    {
        var root = NewRoot();
        try
        {
            using var host = App.App.CreateHost(root);
            var shell = host.Services.GetRequiredService<App.ShellViewModel>();
            await shell.InitializeAsync();
            Assert.Equal(["New Transcription", "Projects", "Review", "Remote server", "Models"], shell.Navigation.Where(page => !page.Advanced && !page.IsSettings).Select(page => page.Name));
            Assert.Equal(["Languages", "Backends", "Benchmark", "Diagnostics", "Terminal"], shell.Navigation.Where(page => page.Advanced).Select(page => page.Name));
            Assert.Equal("New Transcription", shell.Navigation[0].Name);
            Assert.True(shell.Navigation[^1].IsSettings);
            Assert.DoesNotContain(shell.Navigation, page => page.Name == "Queue");
            Assert.False(shell.ShowAdvanced);
            shell.ToggleAdvancedCommand.Execute(null);
            Assert.True(shell.ShowAdvanced);
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task OnlyMissingRequiredFilesAreReportedAndTheCorrectionModelNever()
    {
        var root = NewRoot();
        try
        {
            using var host = App.App.CreateHost(root);
            var shell = host.Services.GetRequiredService<App.ShellViewModel>();
            await shell.InitializeAsync();
            Assert.Equal(shell.Readiness.Length > 0, shell.HasReadinessIssues);
            Assert.DoesNotContain("orrection", shell.Readiness);
            Assert.DoesNotContain("Ready", shell.Readiness); // the list shows problems only, never a checklist of what is fine
        }
        finally { TestCleanup.Delete(root); }
    }
}
