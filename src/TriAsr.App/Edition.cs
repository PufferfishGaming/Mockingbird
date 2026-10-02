using System.IO;

namespace TriAsr.App;

/// <summary>The three programs built from this code (ADR-0014): Studio does everything on one computer, Server holds the models and serves them to other computers, Client is only the window that sends recordings to a server.</summary>
public enum AppEdition { Studio, Server, Client }

/// <summary>
/// Which edition is running. The installer of an edition puts <c>edition.txt</c> next to the program; without it (a build from the repository, a portable folder
/// from before editions) the program is Studio. <c>TRIASR_EDITION</c> overrides the file, so that a test or a screenshot run can start any edition.
/// </summary>
public static class Edition
{
    public const string MarkerFile = "edition.txt";
    private static AppEdition? _current;

    public static AppEdition Current { get => _current ??= Resolve(); set => _current = value; }
    public static bool IsStudio => Current == AppEdition.Studio;
    public static bool IsServer => Current == AppEdition.Server;
    public static bool IsClient => Current == AppEdition.Client;

    /// <summary>Whether this edition has the speech programs and models on this computer. The client does not: it never transcribes anything itself.</summary>
    public static bool RunsEngines => !IsClient;
    /// <summary>Whether the right-hand panel lists the servers on the network and connects to one.</summary>
    public static bool CanConnect => !IsServer;
    /// <summary>Whether this edition can host a server of its own.</summary>
    public static bool CanHost => !IsClient;

    /// <summary>The short name other computers see next to a server ("Studio", "Server").</summary>
    public static string Label => Current.ToString();
    public static string ProductName => "Mockingbird " + Label;
    /// <summary>The folder under the user's local application data. Studio keeps the folder it always had, so that updating does not lose anything.</summary>
    public static string DataFolderName => IsStudio ? "TriASR" : "TriASR-" + Label;
    /// <summary>The folder the installer puts the program in, under Programs.</summary>
    public static string InstallFolderName => DataFolderName;

    /// <summary>Where a downloaded installer waits until it is run.</summary>
    public static string UpdateDirectory => Path.Combine(Path.GetTempPath(), ProductName.Replace(" ", "") + "-Update");

    /// <summary>Only the installed copy can replace itself; a development or portable copy would install a second one.</summary>
    public static bool IsInstalledCopy()
    {
        var path = Environment.ProcessPath;
        if (path is null) return false;
        var installRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", InstallFolderName) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(installRoot, StringComparison.OrdinalIgnoreCase);
    }

    public static AppEdition Parse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "server" => AppEdition.Server,
        "client" => AppEdition.Client,
        _ => AppEdition.Studio
    };

    private static AppEdition Resolve()
    {
        if (Environment.GetEnvironmentVariable("TRIASR_EDITION") is { Length: > 0 } forced) return Parse(forced);
        try
        {
            var marker = Path.Combine(AppContext.BaseDirectory, MarkerFile);
            return File.Exists(marker) ? Parse(File.ReadAllText(marker)) : AppEdition.Studio;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return AppEdition.Studio; }
    }
}
