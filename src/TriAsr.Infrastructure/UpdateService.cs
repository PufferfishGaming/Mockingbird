using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TriAsr.Infrastructure;

/// <summary>The small JSON file published next to every Setup.exe (latest.json).</summary>
public sealed record UpdateManifest(int Schema, string Version, string Url, string Sha256, long Bytes, string? Notes = null);

/// <summary>A validated newer release. <see cref="DownloadUri"/> has already passed the host and path allowlist.</summary>
public sealed record UpdateOffer(Version Version, string VersionText, Uri DownloadUri, string Sha256, long Bytes, string Notes);

/// <summary>
/// Where updates may come from. Production accepts only this project's GitHub release assets over HTTPS.
/// The environment override exists for local end-to-end tests and only accepts a loopback address.
/// </summary>
public sealed record UpdateOptions(Uri ManifestUri, string AllowedHost, string AllowedPathPrefix, bool AllowLoopbackHttp = false, bool IsTestSource = false)
{
    public const string ManifestEnvironmentVariable = "TRIASR_UPDATE_MANIFEST";

    public static UpdateOptions GitHub { get; } = new(
        new Uri("https://github.com/PufferfishGaming/Mockingbird/releases/download/download/latest.json"),
        "github.com", "/PufferfishGaming/Mockingbird/releases/download/");

    public static UpdateOptions FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(ManifestEnvironmentVariable);
        return !string.IsNullOrWhiteSpace(value) && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp
            ? new UpdateOptions(uri, uri.Host, "/", AllowLoopbackHttp: true, IsTestSource: true)
            : GitHub;
    }
}

/// <summary>
/// Checks the release manifest and downloads a newer installer. The installer is never run from here:
/// nothing is handed to the caller until its size and SHA256 match the manifest.
/// </summary>
public sealed partial class UpdateService : IDisposable
{
    public const int MaxManifestBytes = 64 * 1024;
    public const long MaxInstallerBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxNotesLength = 1500;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public UpdateOptions Options { get; }

    public UpdateService(UpdateOptions options, HttpClient? http = null)
    {
        Options = options;
        _ownsClient = http is null;
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex HashPattern();

    /// <summary>Parses "0.1.16", "v0.1.16" or "0.1.16-rc1+build" into a comparable four-part version.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var core = text.Trim().TrimStart('v', 'V');
        var cut = core.IndexOfAny(['-', '+']);
        if (cut >= 0) core = core[..cut];
        if (!Version.TryParse(core, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
        return true;
    }

    /// <summary>Returns the offer when the published release is newer than <paramref name="currentVersion"/>, otherwise null.</summary>
    public async Task<UpdateOffer?> CheckAsync(string currentVersion, CancellationToken token = default)
    {
        if (!TryParseVersion(currentVersion, out var current)) throw new InvalidDataException("The running version number is not valid.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, Options.ManifestUri);
        request.Headers.UserAgent.ParseAdd("MockingbirdStudio/" + current);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxManifestBytes) throw new InvalidDataException("The update information is larger than expected.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxManifestBytes) throw new InvalidDataException("The update information is larger than expected.");
        }
        // Editors and PowerShell commonly save JSON with a UTF-8 byte-order mark, which the JSON reader does not skip on its own.
        ReadOnlySpan<byte> json = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        if (json.StartsWith<byte>([0xEF, 0xBB, 0xBF])) json = json[3..];
        UpdateManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<UpdateManifest>(json, Json); }
        catch (JsonException) { throw new InvalidDataException("The update information could not be read."); }
        var offer = Validate(manifest);
        return offer.Version > current ? offer : null;
    }

    /// <summary>Rejects anything that is not a well-formed release hosted where the options allow.</summary>
    public UpdateOffer Validate(UpdateManifest? manifest)
    {
        if (manifest is null || manifest.Schema != 1) throw new InvalidDataException("The update information uses an unsupported format.");
        if (!TryParseVersion(manifest.Version, out var version)) throw new InvalidDataException("The update version number is not valid.");
        if (!Uri.TryCreate(manifest.Url, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("The update download address is not valid.");
        var secure = uri.Scheme == Uri.UriSchemeHttps || (Options.AllowLoopbackHttp && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
        if (!secure) throw new InvalidDataException("The update must be downloaded over HTTPS.");
        if (!uri.Host.Equals(Options.AllowedHost, StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith(Options.AllowedPathPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The update is not hosted in the project's release location.");
        if (!HashPattern().IsMatch(manifest.Sha256 ?? "")) throw new InvalidDataException("The update checksum is not valid.");
        if (manifest.Bytes is < 1 or > MaxInstallerBytes) throw new InvalidDataException("The update size is not valid.");
        var notes = (manifest.Notes ?? "").Trim();
        if (notes.Length > MaxNotesLength) notes = notes[..MaxNotesLength].TrimEnd() + "…";
        var text = version.Revision == 0 ? $"{version.Major}.{version.Minor}.{version.Build}" : version.ToString();
        return new UpdateOffer(version, text, uri, manifest.Sha256!.ToUpperInvariant(), manifest.Bytes, notes);
    }

    /// <summary>
    /// Downloads the installer to <paramref name="directory"/> and returns its path only after the byte count
    /// and SHA256 match. A failed or cancelled download leaves no installer behind.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateOffer offer, string directory, IProgress<DownloadProgress>? progress = null, CancellationToken token = default)
    {
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"Mockingbird-Studio-Setup-{offer.VersionText}.exe");
        var partial = destination + ".partial";
        TryDelete(partial); TryDelete(destination);
        var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;
        if (free < offer.Bytes + 256L * 1024 * 1024) throw new IOException("There is not enough free disk space to download the update.");
        var completed = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, offer.DownloadUri);
            request.Headers.UserAgent.ParseAdd("MockingbirdStudio-Updater");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != offer.Bytes)
                throw new InvalidDataException("The update download is not the size the release says it should be.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            {
                var buffer = new byte[128 * 1024];
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var previous = TimeSpan.Zero;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    received += read;
                    if (received > offer.Bytes) throw new InvalidDataException("The update download is larger than the release says it should be.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    if (clock.Elapsed - previous > TimeSpan.FromMilliseconds(250))
                    { progress?.Report(new(received, offer.Bytes, received / Math.Max(.001, clock.Elapsed.TotalSeconds))); previous = clock.Elapsed; }
                }
                await output.FlushAsync(token);
                progress?.Report(new(received, offer.Bytes, received / Math.Max(.001, clock.Elapsed.TotalSeconds)));
            }
            if (received != offer.Bytes) throw new EndOfStreamException("The update download was incomplete.");
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(offer.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded update failed its SHA256 check and was discarded.");
            File.Move(partial, destination);
            completed = true;
            return destination;
        }
        finally { if (!completed) TryDelete(partial); }
    }

    /// <summary>Best-effort removal of installers left over from earlier update attempts.</summary>
    public static void RemoveStaleDownloads(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "Mockingbird-Studio-Setup-*"))
            TryDelete(file);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
