using TriAsr.Application;
using TriAsr.Infrastructure;

namespace TriAsr.Persistence.Tests;

public sealed class ProcessMetricsTests
{
    private static ProcessRequest Script(string command) =>
        new(InteractiveTerminal.ShellExecutable, ["-NoProfile", "-Command", command], Path.GetTempPath(), TimeSpan.FromSeconds(60));

    [Fact]
    public async Task ABusyProcessReportsItsProcessorTimeAndPeakMemory()
    {
        var feed = new ActivityFeed();
        var result = await new ProcessRunner(feed).RunAsync(Script("$s = [Diagnostics.Stopwatch]::StartNew(); while ($s.Elapsed.TotalSeconds -lt 1.5) { }"));
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.CpuSeconds);
        Assert.InRange(result.CpuSeconds!.Value, 0.5, 60);
        Assert.True(result.PeakRamBytes > 0);
        var exit = Assert.Single(feed.Snapshot(), entry => entry.Category == "exit");
        Assert.Contains("CPU ", exit.Message);
        Assert.Contains("peak ", exit.Message);
        Assert.Contains(" MB", exit.Message);
    }

    [Fact]
    public async Task WaitingIsNotCountedAsProcessorTime()
    {
        var result = await new ProcessRunner().RunAsync(Script("Start-Sleep -Seconds 2"));
        Assert.True(result.Seconds >= 2);
        // PowerShell's own start-up costs some CPU, but sleeping must be far below the elapsed time.
        Assert.True(result.CpuSeconds < result.Seconds - 0.8, $"CPU {result.CpuSeconds} s versus {result.Seconds} s elapsed");
    }

    [Fact]
    public async Task AFailingProcessStillReportsItsMetricsAndExitCode()
    {
        var result = await new ProcessRunner().RunAsync(Script("exit 3"));
        Assert.Equal(3, result.ExitCode);
        Assert.NotNull(result.CpuSeconds);
    }
}
