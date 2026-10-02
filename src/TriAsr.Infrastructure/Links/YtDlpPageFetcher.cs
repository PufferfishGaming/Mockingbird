using System.Globalization;
using TriAsr.Application;

namespace TriAsr.Infrastructure;

/// <summary>
/// Fetches the sound of a page with yt-dlp: a video on one of the many sites that host them, a podcast episode, a recording on a news page.
/// Only the best audio (or, where a site offers no audio of its own, the video) of the one page is taken; playlists are not followed.
/// </summary>
public sealed class YtDlpPageFetcher(YtDlpTool tool, IProcessRunner runner)
{
    private const string FileMark = "MBFILE ";
    private const string TitleMark = "MBTITLE ";
    private const string ProgressMark = "MBPROGRESS ";

    public async Task<FetchedLink> FetchAsync(Uri link, string folder, LinkFetchOptions options, IProgress<double>? percent, CancellationToken token)
    {
        if (!tool.Installed) throw new LinkException(LinkMessages.NeedsHelper);
        Directory.CreateDirectory(folder);
        string? file = null, title = null;
        var errors = new List<string>();
        void Line(string raw)
        {
            var line = raw.TrimEnd('\r', '\n');
            if (line.StartsWith(FileMark, StringComparison.Ordinal)) file = line[FileMark.Length..].Trim();
            else if (line.StartsWith(TitleMark, StringComparison.Ordinal)) title = line[TitleMark.Length..].Trim();
            else if (line.StartsWith(ProgressMark, StringComparison.Ordinal)) { if (ParseProgress(line[ProgressMark.Length..]) is { } value) percent?.Report(value); }
            else if (line.StartsWith("ERROR:", StringComparison.Ordinal)) errors.Add(line);
        }

        var result = await runner.RunAsync(new ProcessRequest(tool.ExecutablePath, Arguments(link, folder, options), folder, TimeSpan.FromHours(3), Line), token).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new LinkException(Explain(errors.LastOrDefault()));
        var path = file is { Length: > 0 } && File.Exists(file) && IsInside(folder, file) ? file : Newest(folder);
        if (path is null) throw new LinkException(LinkMessages.NoSound);
        percent?.Report(100);
        return new FetchedLink(path, string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(path) : title!);
    }

    public static IReadOnlyList<string> Arguments(Uri link, string folder, LinkFetchOptions options) =>
    [
        "--ignore-config", "--no-playlist", "--no-warnings", "--color", "never", "--newline", "--no-mtime", "--windows-filenames",
        "--format", "bestaudio/best",
        "--max-filesize", options.Limit.ToString(CultureInfo.InvariantCulture),
        "--socket-timeout", "30", "--retries", "3",
        "--progress-template", $"download:{ProgressMark}%(progress.downloaded_bytes)s %(progress.total_bytes)s %(progress.total_bytes_estimate)s",
        "--print", $"after_move:{FileMark}%(filepath)s", "--print", $"after_move:{TitleMark}%(title)s", "--no-simulate",
        "--output", Path.Combine(folder, "%(title).80B [%(id).40B].%(ext)s"),
        "--", link.AbsoluteUri
    ];

    /// <summary>Reads "downloaded total estimate" (numbers, or NA where yt-dlp does not know): the share downloaded, when a total is known.</summary>
    public static double? ParseProgress(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var done)) return null;
        foreach (var candidate in parts.Skip(1).Take(2))
            if (double.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out var total) && total > 0)
                return Math.Clamp(done * 100 / total, 0, 100);
        return null;
    }

    /// <summary>Turns what yt-dlp complained about into a sentence the person can use. Anything it does not recognise is passed on as the site's own words.</summary>
    public static string Explain(string? error)
    {
        var text = (error ?? "").Replace("ERROR:", "").Trim();
        if (text.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase) || text.Contains("No video formats found", StringComparison.OrdinalIgnoreCase)
            || text.Contains("no suitable", StringComparison.OrdinalIgnoreCase)) return LinkMessages.NoSound;
        if (text.Contains("Sign in", StringComparison.OrdinalIgnoreCase) || text.Contains("login", StringComparison.OrdinalIgnoreCase)
            || text.Contains("private", StringComparison.OrdinalIgnoreCase) || text.Contains("members-only", StringComparison.OrdinalIgnoreCase)
            || text.Contains("confirm you", StringComparison.OrdinalIgnoreCase)) return LinkMessages.NeedsLogin;
        if (text.Contains("larger than max-filesize", StringComparison.OrdinalIgnoreCase) || text.Contains("max-filesize", StringComparison.OrdinalIgnoreCase))
            return LinkMessages.TooLarge;
        if (text.Contains("Unable to download", StringComparison.OrdinalIgnoreCase) || text.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || text.Contains("getaddrinfo", StringComparison.OrdinalIgnoreCase)) return LinkMessages.Unreachable;
        return text.Length == 0 ? LinkMessages.Failed : LinkMessages.FailedPrefix + (text.Length > 300 ? text[..300] : text);
    }

    private static bool IsInside(string folder, string file) =>
        Path.GetFullPath(file).StartsWith(Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string? Newest(string folder) =>
        new DirectoryInfo(folder).EnumerateFiles().Where(file => !file.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                && !file.Name.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault()?.FullName;
}
