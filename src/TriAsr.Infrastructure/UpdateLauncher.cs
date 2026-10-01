using System.Diagnostics;
using System.Text;

namespace TriAsr.Infrastructure;

/// <summary>
/// Hands a verified installer to a small hidden PowerShell helper that outlives the app: it waits for the app
/// to exit, runs the installer, records the outcome, removes the installer and starts the app again.
/// </summary>
public static class UpdateLauncher
{
    public static string BuildScript(string setupPath, string appPath, int waitForProcessId, string resultPath, string version, string? appArguments = null)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var relaunch = string.IsNullOrWhiteSpace(appArguments)
            ? $"Start-Process -FilePath {Quote(appPath)}"
            : $"Start-Process -FilePath {Quote(appPath)} -ArgumentList {Quote(appArguments)}";
        return string.Join("\n",
            "$ErrorActionPreference = 'SilentlyContinue'",
            $"Wait-Process -Id {waitForProcessId} -Timeout 120",
            "$code = -1",
            $"$installer = Start-Process -FilePath {Quote(setupPath)} -ArgumentList '/passive','/norestart' -PassThru -Wait",
            "if ($installer) { $code = $installer.ExitCode }",
            $"New-Item -ItemType Directory -Force -Path (Split-Path -Parent {Quote(resultPath)}) | Out-Null",
            $"[pscustomobject]@{{ version = {Quote(version)}; exitCode = $code }} | ConvertTo-Json -Compress | Set-Content -LiteralPath {Quote(resultPath)} -Encoding UTF8",
            $"Remove-Item -LiteralPath {Quote(setupPath)} -Force",
            relaunch);
    }

    /// <summary>Starts the helper. The caller must close the app right afterwards so the installer can replace its files.</summary>
    public static void Start(string setupPath, string appPath, int waitForProcessId, string resultPath, string version, string? appArguments = null)
    {
        if (!File.Exists(setupPath)) throw new FileNotFoundException("The verified installer is missing.", setupPath);
        var script = BuildScript(Path.GetFullPath(setupPath), Path.GetFullPath(appPath), waitForProcessId, Path.GetFullPath(resultPath), version, appArguments);
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        using var helper = Process.Start(start) ?? throw new InvalidOperationException("The update helper could not be started.");
    }
}
