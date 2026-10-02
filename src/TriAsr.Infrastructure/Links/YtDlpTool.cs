using System.Security.Cryptography;
using System.Text.Json;
using TriAsr.Application;

namespace TriAsr.Infrastructure;

/// <summary>
/// The helper that fetches the sound of web pages: yt-dlp, a program of its own project that knows how to get a recording out of well over a thousand
/// sites (ADR-0018). It is not shipped with Mockingbird. On the user's request it is downloaded once from the project's releases, checked against the
/// SHA-256 the same release publishes, and kept in the program's data folder; "update" does the same again when the checksum has changed.
/// </summary>
public sealed class YtDlpTool : ILinkTool, IDisposable
{
    public const string ReleaseFolder = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";
    private const string FileName = "yt-dlp.exe";
    private const string SumsName = "SHA2-256SUMS";
    private const long MaxBytes = 200L * 1024 * 1024;                 // the real file is about 20 MB; this only stops something absurd

    private readonly string _folder;
    private readonly string _release;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _operation = new(1, 1);

    private sealed record Receipt(string Sha256, long Bytes, long LastWriteTicks);

    /// <param name="folder">Where the program is kept.</param>
    /// <param name="release">The address of the folder the files are published in (with a closing slash). The tests point it at a server of their own.</param>
    public YtDlpTool(string folder, string? release = null, HttpClient? http = null)
    {
        _folder = Path.GetFullPath(folder);
        _release = release ?? ReleaseFolder;
        if (!_release.EndsWith('/')) _release += "/";
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public string ExecutablePath => Path.Combine(_folder, FileName);

    private string ReceiptPath => ExecutablePath + ".json";

    /// <summary>Installed means the file is there and is the one that was checked when it came: it has the size and the date the receipt wrote down.</summary>
    public bool Installed => ReadReceipt() is not null;

    private Receipt? ReadReceipt()
    {
        var file = new FileInfo(ExecutablePath);
        if (!file.Exists) return null;
        try
        {
            var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(ReceiptPath));
            return receipt is not null && receipt.Bytes == file.Length && receipt.LastWriteTicks == file.LastWriteTimeUtc.Ticks ? receipt : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public async Task<bool> UpdateAvailableAsync(CancellationToken token)
    {
        var installed = ReadReceipt();
        if (installed is null) return false;
        var published = await PublishedHashAsync(token).ConfigureAwait(false);
        return !published.Equals(installed.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> InstallAsync(IProgress<double>? percent, CancellationToken token)
    {
        await _operation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var expected = await PublishedHashAsync(token).ConfigureAwait(false);
            if (ReadReceipt() is { } current && current.Sha256.Equals(expected, StringComparison.OrdinalIgnoreCase)) return false;
            Directory.CreateDirectory(_folder);
            var partial = ExecutablePath + ".partial";
            try
            {
                string actual;
                try
                {
                    using var response = await _http.GetAsync(_release + FileName, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) throw new LinkException(LinkMessages.HelperDownload);
                    var total = response.Content.Headers.ContentLength;
                    if (total > MaxBytes) throw new LinkException(LinkMessages.HelperDownload);
                    await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[128 * 1024];
                    long received = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        received += read;
                        if (received > MaxBytes) throw new LinkException(LinkMessages.HelperDownload);
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        if (total > 0) percent?.Report(Math.Min(100, received * 100.0 / total.Value));
                    }
                    await output.FlushAsync(token).ConfigureAwait(false);
                    actual = Convert.ToHexString(hash.GetHashAndReset());
                }
                catch (HttpRequestException error) { throw new LinkException(LinkMessages.HelperDownload, error); }
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new LinkException(LinkMessages.HelperChecksum);
                try { File.Move(partial, ExecutablePath, true); }
                catch (IOException error) { throw new LinkException(LinkMessages.HelperInUse, error); }
                var file = new FileInfo(ExecutablePath);
                var temporary = ReceiptPath + ".tmp";
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new Receipt(expected, file.Length, file.LastWriteTimeUtc.Ticks)), token).ConfigureAwait(false);
                File.Move(temporary, ReceiptPath, true);
                percent?.Report(100);
                return true;
            }
            finally { try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        finally { _operation.Release(); }
    }

    /// <summary>The SHA-256 the project publishes for the Windows program, from the list that goes with each release.</summary>
    private async Task<string> PublishedHashAsync(CancellationToken token)
    {
        string text;
        try
        {
            using var response = await _http.GetAsync(_release + SumsName, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new LinkException(LinkMessages.HelperDownload);
            if (response.Content.Headers.ContentLength > 1024 * 1024) throw new LinkException(LinkMessages.HelperDownload);
            text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }
        catch (HttpRequestException error) { throw new LinkException(LinkMessages.HelperDownload, error); }
        return ParseHash(text) ?? throw new LinkException(LinkMessages.HelperDownload);
    }

    /// <summary>Finds the line "&lt;64 hex digits&gt;  yt-dlp.exe" in the checksum list. The other programs in the list (other systems, other builds) are not looked at.</summary>
    public static string? ParseHash(string sums)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split([' ', '\t', '*'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1] == FileName && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit)) return parts[0].ToUpperInvariant();
        }
        return null;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        _operation.Dispose();
    }
}
