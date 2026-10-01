using System.Diagnostics;
using System.Text;

namespace TriAsr.Infrastructure;

public sealed class InteractiveTerminal(ActivityFeed activity) : IDisposable
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _marker = "TRIASR_READY_" + Guid.NewGuid().ToString("N") + ":";
    private Process? _process;
    private WorkerOwnership? _ownership;
    private Task? _lifetime;
    private TaskCompletionSource _completed = ReadySource();
    private volatile bool _running;
    private volatile bool _busy;
    private string _directory = "";
    public ActivityFeed Output { get; } = new();
    public bool IsRunning => _running;
    public bool IsBusy => _busy;
    public string CurrentDirectory => _directory;
    public static string ShellExecutable
    {
        get
        {
            var pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
            return File.Exists(pwsh) ? pwsh : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        }
    }
    private static TaskCompletionSource ReadySource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Write(string message) { Output.Append("PowerShell", message); activity.Append("terminal", message); }

    public async Task StartAsync(string directory, CancellationToken token = default)
    {
        await _writeGate.WaitAsync(token);
        try
        {
            if (_running) return;
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The terminal's starting folder does not exist.");
            var escapedDirectory = directory.Replace("'", "''", StringComparison.Ordinal);
            var script = $$"""
                $ProgressPreference = 'SilentlyContinue'
                [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
                [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
                $OutputEncoding = [Console]::OutputEncoding
                Set-Location -LiteralPath '{{escapedDirectory}}'
                function global:Write-TriAsrPrompt { [Console]::WriteLine('{{_marker}}' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((Get-Location).Path))) }
                Write-TriAsrPrompt
                while ($null -ne ($triasrCommand = [Console]::ReadLine())) {
                    try {
                        $triasrScript = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($triasrCommand))
                        Invoke-Expression $triasrScript *>&1 | Out-String -Stream | ForEach-Object { [Console]::WriteLine($_) }
                    } catch { [Console]::WriteLine(($_ | Out-String)) }
                    finally { Write-TriAsrPrompt }
                }
                """;
            var info = new ProcessStartInfo(ShellExecutable)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-OutputFormat", "Text", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) info.ArgumentList.Add(argument);
            var process = new Process { StartInfo = info };
            if (!process.Start()) throw new InvalidOperationException("PowerShell could not start.");
            _process = process;
            try { _ownership = WorkerOwnership.TryAttach(process); }
            catch { process.Dispose(); _process = null; throw; }
            _running = true; _busy = true; _directory = directory; _completed = ReadySource();
            Write("Starting " + Path.GetFileName(ShellExecutable) + " in " + directory);
            _lifetime = MonitorAsync(process, PumpAsync(process.StandardOutput), PumpAsync(process.StandardError));
            await _completed.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
        }
        finally { _writeGate.Release(); }
    }
    public async Task RunCommandAsync(string command, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        await _writeGate.WaitAsync(token);
        try
        {
            if (!_running || _process is null) throw new InvalidOperationException("Start PowerShell first.");
            if (_busy) throw new InvalidOperationException("A command is running. Send input or stop the session first.");
            _completed = ReadySource(); _busy = true;
            Write("PS " + _directory + "> " + command);
            await _process.StandardInput.WriteLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(command)).AsMemory(), token);
            await _process.StandardInput.FlushAsync(token);
        }
        finally { _writeGate.Release(); }
    }
    public async Task SendInputAsync(string input, CancellationToken token = default)
    {
        await _writeGate.WaitAsync(token);
        try
        {
            if (!_running || !_busy || _process is null) throw new InvalidOperationException("No running command is waiting for input.");
            Write("stdin> " + input);
            await _process.StandardInput.WriteLineAsync(input.AsMemory(), token); await _process.StandardInput.FlushAsync(token);
        }
        finally { _writeGate.Release(); }
    }
    public Task WaitForIdleAsync(TimeSpan timeout, CancellationToken token = default) => _completed.Task.WaitAsync(timeout, token);
    private async Task PumpAsync(StreamReader reader)
    {
        var pending = new StringBuilder(); var buffer = new char[2048];
        while (await reader.ReadAsync(buffer) is var count && count > 0)
        {
            foreach (var character in buffer.AsSpan(0, count))
            {
                if (character == '\n') { Receive(pending.ToString().TrimEnd('\r')); pending.Clear(); }
                else pending.Append(character);
            }
            // Prompt text may not end in a newline. Emit it promptly instead of waiting for the command to exit.
            if (pending.Length > 0 && !HasPendingMarker(pending.ToString()))
            { Write(pending.ToString().TrimEnd('\r')); pending.Clear(); }
            if (pending.Length > 8192) { Write(pending.ToString()); pending.Clear(); }
        }
        if (pending.Length > 0) Receive(pending.ToString());
    }
    private void Receive(string line)
    {
        var markerAt = line.IndexOf(_marker, StringComparison.Ordinal);
        if (markerAt >= 0)
        {
            if (markerAt > 0) Write(line[..markerAt]);
            try { _directory = Encoding.UTF8.GetString(Convert.FromBase64String(line[(markerAt + _marker.Length)..])); }
            catch (FormatException) { Write("Could not read the PowerShell working folder."); }
            _busy = false; Write("PS " + _directory + "> ready"); _completed.TrySetResult();
        }
        else Write(line);
    }
    private bool HasPendingMarker(string text) => text.Contains(_marker, StringComparison.Ordinal) ||
        Enumerable.Range(1, Math.Min(text.Length, _marker.Length)).Any(length => _marker.StartsWith(text[^length..], StringComparison.Ordinal));
    private async Task MonitorAsync(Process process, Task stdout, Task stderr)
    {
        try { await process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr); Write("PowerShell exited with code " + process.ExitCode + ". Start a new session to continue."); }
        catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException) { Write("PowerShell session ended: " + error.Message); }
        finally
        {
            _completed.TrySetException(new InvalidOperationException("PowerShell session ended."));
            await _writeGate.WaitAsync();
            try { _running = false; _busy = false; _ownership?.Dispose(); _ownership = null; _process = null; process.Dispose(); }
            finally { _writeGate.Release(); }
        }
    }
    public async Task StopAsync()
    {
        await _writeGate.WaitAsync();
        try { KillOwnedSession(); }
        finally { _writeGate.Release(); }
        if (_lifetime is { } lifetime) await lifetime.WaitAsync(TimeSpan.FromSeconds(10));
    }
    private void KillOwnedSession()
    {
        _ownership?.Dispose();
        try { if (_process is { HasExited: false } process) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    public void Dispose() => KillOwnedSession();
}
