using System.Diagnostics;
using TriAsr.Application;
using TriAsr.Infrastructure;

namespace TriAsr.Persistence.Tests;
public sealed class TerminalTests
{
    [Fact]
    public void ActivityIsBoundedAndInternalApiKeysAreRedactedFromCommandsAndOutput()
    {
        var feed = new ActivityFeed();
        var command = feed.DescribeCommand("llama-server.exe", ["--api-key", "private-test-key", "--model", "model path"]);
        feed.Append("start", command); feed.Append("out", "echo private-test-key");
        Assert.All(feed.Snapshot(), entry => Assert.DoesNotContain("private-test-key", entry.Message));
        for (var index = 0; index < 2100; index++) feed.Append("test", index.ToString());
        Assert.Equal(ActivityFeed.Capacity, feed.Snapshot().Count);
        Assert.Equal("2099", feed.Snapshot()[^1].Message);
    }
    [Fact]
    public async Task NativeProcessOutputAndExitCodesReachActivityWithoutBreakingItsObserver()
    {
        var feed = new ActivityFeed(); var observed = new List<string>();
        var runner = new ProcessRunner(feed);
        var result = await runner.RunAsync(new(InteractiveTerminal.ShellExecutable,
            ["-NoProfile", "-Command", "[Console]::WriteLine('native-out'); [Console]::Error.WriteLine('native-err'); exit 7"],
            Path.GetTempPath(), TimeSpan.FromSeconds(20), line => { lock (observed) observed.Add(line); }));
        Assert.Equal(7, result.ExitCode);
        Assert.Contains(feed.Snapshot(), entry => entry.Message.Contains("native-out") && entry.Category.EndsWith("/out"));
        Assert.Contains(feed.Snapshot(), entry => entry.Message.Contains("native-err") && entry.Category.EndsWith("/err"));
        Assert.Contains(feed.Snapshot(), entry => entry.Category == "exit" && entry.Message.Contains("code 7"));
        Assert.Contains(observed, line => line.TrimEnd('\r') == "native-out"); Assert.Contains(observed, line => line.TrimEnd('\r') == "native-err");
    }
    [Fact]
    public async Task PowerShellPreservesVariablesFolderMultilineCommandsAndReadsPromptInput()
    {
        var root = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "folder with spaces"));
        using var terminal = new InteractiveTerminal(new ActivityFeed());
        try
        {
            await terminal.StartAsync(root);
            await terminal.RunCommandAsync("$savedValue = 41\nSet-Location -LiteralPath 'folder with spaces'\nWrite-Output ($savedValue + 1)\nWrite-Output 'Unicode: árvíztűrő tükörfúrógép'");
            await terminal.WaitForIdleAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(Path.Combine(root, "folder with spaces"), terminal.CurrentDirectory);
            Assert.Contains(terminal.Output.Snapshot(), entry => entry.Message.Trim() == "42");
            Assert.Contains(terminal.Output.Snapshot(), entry => entry.Message == "Unicode: árvíztűrő tükörfúrógép");
            Assert.DoesNotContain(terminal.Output.Snapshot(), entry => entry.Message.Contains("#< CLIXML"));
            await terminal.RunCommandAsync("$response = Read-Host 'Test prompt'; Write-Output ('reply=' + $response + '; value=' + $savedValue)");
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (!terminal.Output.Snapshot().Any(entry => entry.Message.Contains("Test prompt")) && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
            Assert.True(terminal.IsBusy);
            await terminal.SendInputAsync("test response"); await terminal.WaitForIdleAsync(TimeSpan.FromSeconds(10));
            Assert.Contains(terminal.Output.Snapshot(), entry => entry.Message.Contains("reply=test response; value=41"));
            await terminal.RunCommandAsync("Write-Output 'after-prompt'"); await terminal.WaitForIdleAsync(TimeSpan.FromSeconds(10));
            Assert.Contains(terminal.Output.Snapshot(), entry => entry.Message == "after-prompt");
        }
        finally
        {
            await terminal.StopAsync();
            Assert.StartsWith(Path.Combine(Path.GetTempPath(), "TriAsr.Tests") + Path.DirectorySeparatorChar, Path.GetFullPath(root));
            Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task StoppingSessionTerminatesItsOwnedChildAndAllowsARealRestart()
    {
        using var terminal = new InteractiveTerminal(new ActivityFeed());
        await terminal.StartAsync(Path.GetTempPath());
        await terminal.RunCommandAsync("$child = Start-Process powershell.exe -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 60' -WindowStyle Hidden -PassThru; Write-Output ('owned-child=' + $child.Id); Start-Sleep -Seconds 60");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        ActivityEntry? entry;
        while ((entry = terminal.Output.Snapshot().FirstOrDefault(line => line.Message.StartsWith("owned-child="))) is null && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
        Assert.NotNull(entry);
        var childId = int.Parse(entry.Message["owned-child=".Length..]);
        await terminal.StopAsync(); Assert.False(terminal.IsRunning);
        try { using var child = Process.GetProcessById(childId); Assert.True(child.HasExited); }
        catch (ArgumentException) { }
        await terminal.StartAsync(Path.GetTempPath()); await terminal.RunCommandAsync("Write-Output 'restarted'");
        await terminal.WaitForIdleAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(terminal.Output.Snapshot(), line => line.Message == "restarted");
        await terminal.StopAsync();
    }
}
