using TriAsr.Application;
using TriAsr.Infrastructure;

namespace TriAsr.Persistence.Tests;

public sealed class WorkerPriorityTests
{
    private const string ReportPriority = "(Get-Process -Id $PID).PriorityClass";

    private static ProcessRequest Script(string command) =>
        new(InteractiveTerminal.ShellExecutable, ["-NoProfile", "-Command", command], Path.GetTempPath(), TimeSpan.FromSeconds(60));

    [Fact]
    public async Task EnginesAndToolsStartedByTheRunnerRunAtBelowNormalPriority()
    {
        var result = await new ProcessRunner().RunAsync(Script(ReportPriority));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("BelowNormal", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task ProcessesAnEngineStartsItselfInheritTheLowerPriority()
    {
        var result = await new ProcessRunner().RunAsync(Script($"& '{InteractiveTerminal.ShellExecutable}' -NoProfile -Command '{ReportPriority}'"));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("BelowNormal", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task TheInteractiveTerminalKeepsNormalPriorityForCommandsYouTypeYourself()
    {
        using var terminal = new InteractiveTerminal(new ActivityFeed());
        try
        {
            await terminal.StartAsync(Path.GetTempPath());
            await terminal.RunCommandAsync(ReportPriority);
            await terminal.WaitForIdleAsync(TimeSpan.FromSeconds(15));
            Assert.Contains(terminal.Output.Snapshot(), entry => entry.Message.Trim() == "Normal");
            Assert.DoesNotContain(terminal.Output.Snapshot(), entry => entry.Message.Trim() == "BelowNormal");
        }
        finally { await terminal.StopAsync(); }
    }
}
