using System.Diagnostics;
using System.Text;
using TriAsr.Application;

namespace TriAsr.Infrastructure;

public sealed class ProcessRunner(ActivityFeed? activity = null) : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(request.Executable)
        {
            WorkingDirectory = request.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in request.Arguments) info.ArgumentList.Add(argument);
        activity?.Append("start", activity.DescribeCommand(request.Executable, request.Arguments));
        using var process = new Process { StartInfo = info };
        var stopwatch = Stopwatch.StartNew();
        if (!process.Start()) throw new InvalidOperationException("Worker could not start.");
        using var ownership = WorkerOwnership.TryAttach(process, ProcessPriorityClass.BelowNormal);
        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var name = Path.GetFileName(request.Executable);
        var stdout = DrainAsync(process.StandardOutput, line => { activity?.Append(name + "/out", line); request.ErrorLine?.Invoke(line); });
        var stderr = DrainAsync(process.StandardError, line => { activity?.Append(name + "/err", line); request.ErrorLine?.Invoke(line); });
        using var sampling = new CancellationTokenSource();
        long peakRam = 0;
        var memorySampling = Task.Run(async () =>
        {
            try
            {
                while (!sampling.IsCancellationRequested)
                {
                    try { process.Refresh(); peakRam = Math.Max(peakRam, process.WorkingSet64); }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    await Task.Delay(100, sampling.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
        try { await process.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            activity?.Append("stop", name + (cancellationToken.IsCancellationRequested ? " cancelled" : " timed out"));
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"Worker exceeded {request.Timeout.TotalSeconds:0} seconds.");
        }
        finally { sampling.Cancel(); await memorySampling.ConfigureAwait(false); }
        double? cpuSeconds = null;
        try { cpuSeconds = process.TotalProcessorTime.TotalSeconds; }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        var result = new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), stopwatch.Elapsed.TotalSeconds, peakRam > 0 ? peakRam : null, cpuSeconds);
        activity?.Append("exit", $"{name} · code {result.ExitCode} · {result.Seconds:0.00}s" + (result.CpuSeconds is { } cpu ? $" · CPU {cpu:0.00}s" : "") + (result.PeakRamBytes is { } peak ? $" · peak {peak / 1048576d:0} MB" : ""));
        return result;
    }

    private static async Task<string> DrainAsync(System.IO.StreamReader reader, Action<string>? observer = null)
    {
        const int maximum = 2 * 1024 * 1024;
        var content = new StringBuilder();
        var pending = new StringBuilder();
        var buffer = new char[8192];
        while (await reader.ReadAsync(buffer).ConfigureAwait(false) is var count && count > 0)
        {
            content.Append(buffer, 0, count);
            if (observer is not null)
            {
                pending.Append(buffer, 0, count);
                var lines = pending.ToString().Split('\n');
                for (var i = 0; i < lines.Length - 1; i++) observer(lines[i]);
                pending.Clear(); pending.Append(lines[^1]);
                if (pending.Length > maximum) pending.Remove(0, pending.Length - maximum);
            }
            if (content.Length > maximum) content.Remove(0, content.Length - maximum);
        }
        if (pending.Length > 0) observer?.Invoke(pending.ToString());
        return content.ToString();
    }
}
