using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using TriAsr.Application;

namespace TriAsr.Infrastructure;

/// <summary>
/// Downloads a link that leads straight to an audio or video file (an mp3 on a podcast host, a recording on a web server). It needs no helper program.
/// A link that leads to a page returns <c>null</c> so that the page helper can have a go (ADR-0018).
/// </summary>
public sealed class DirectLinkDownloader : IDisposable
{
    private const int MaxRedirects = 8;
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(60);
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".m4b", ".aac", ".flac", ".ogg", ".oga", ".opus", ".wma", ".aif", ".aiff", ".amr", ".weba",
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv", ".mpg", ".mpeg", ".3gp", ".ts"
    };

    private readonly HttpClient _public;       // refuses to connect to any address inside a private network
    private readonly HttpClient _any;
    private readonly bool _owns;
    private readonly Func<Uri, bool, CancellationToken, Task> _gate;

    /// <param name="any">A client for the tests; when given it is used for both kinds of link.</param>
    /// <param name="gate">The check every address goes through before it is requested, the first one and each redirect (<see cref="LinkPolicy.EnsureAllowedAsync"/>). The tests replace it.</param>
    public DirectLinkDownloader(HttpClient? any = null, Func<Uri, bool, CancellationToken, Task>? gate = null)
    {
        _gate = gate ?? LinkPolicy.EnsureAllowedAsync;
        _owns = any is null;
        _any = any ?? Create(restrict: false);
        _public = any ?? Create(restrict: true);
    }

    private static HttpClient Create(bool restrict)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2), ConnectTimeout = TimeSpan.FromSeconds(20) };
        if (restrict) handler.ConnectCallback = ConnectToPublicAddressAsync;
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mockingbird (link transcription)");
        return client;
    }

    /// <summary>Resolves the name itself and connects only to a public address, so that a name that points into a private network cannot be reached even if it changes its mind between the check and the connection.</summary>
    private static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
        var allowed = addresses.Where(LinkPolicy.IsPublic).ToArray();
        if (allowed.Length == 0) throw new LinkException(LinkMessages.PrivateNetwork);
        Exception? last = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException error) { socket.Dispose(); last = error; }
            catch { socket.Dispose(); throw; }
        }
        throw new LinkException(LinkMessages.Unreachable, last);
    }

    /// <returns>The downloaded file, or null when the address does not lead to an audio or video file.</returns>
    public async Task<FetchedLink?> TryAsync(Uri link, string folder, LinkFetchOptions options, IProgress<double>? percent, CancellationToken token)
    {
        var client = options.AllowPrivateNetwork ? _any : _public;
        var current = link;
        for (var hop = 0; ; hop++)
        {
            await _gate(current, options.AllowPrivateNetwork, token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.ParseAdd("audio/*, video/*;q=0.9, */*;q=0.5");
            HttpResponseMessage response;
            try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
            catch (HttpRequestException error) when (error.InnerException is LinkException inner) { throw inner; }
            catch (HttpRequestException error) { throw new LinkException(LinkMessages.Unreachable, error); }
            using (response)
            {
                if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (response.Headers.Location is not { } location || hop >= MaxRedirects) throw new LinkException(LinkMessages.TooManyRedirects);
                    current = LinkPolicy.Parse((location.IsAbsoluteUri ? location : new Uri(current, location)).AbsoluteUri);
                    continue;
                }
                if (!response.IsSuccessStatusCode) return null;
                if (!LooksLikeMedia(response, current)) return null;
                return await SaveAsync(response, current, folder, options, percent, token).ConfigureAwait(false);
            }
        }
    }

    internal static bool LooksLikeMedia(HttpResponseMessage response, Uri address)
    {
        var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
        if (type.StartsWith("audio/", StringComparison.Ordinal) || type.StartsWith("video/", StringComparison.Ordinal) || type == "application/ogg") return true;
        if (type is "application/octet-stream" or "binary/octet-stream" or "" or "application/force-download")
            return MediaExtensions.Contains(Path.GetExtension(address.AbsolutePath)) || MediaExtensions.Contains(Path.GetExtension(SuggestedName(response) ?? ""));
        return false;
    }

    private static string? SuggestedName(HttpResponseMessage response)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        return disposition?.FileNameStar ?? disposition?.FileName?.Trim('"');
    }

    private static async Task<FetchedLink> SaveAsync(HttpResponseMessage response, Uri address, string folder, LinkFetchOptions options, IProgress<double>? percent, CancellationToken token)
    {
        var total = response.Content.Headers.ContentLength;
        if (total > options.Limit) throw new LinkException(LinkMessages.TooLarge);
        Directory.CreateDirectory(folder);
        var name = FileNameFor(response, address);
        var destination = Unique(folder, name);
        var partial = destination + ".partial";
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                using var stalled = CancellationTokenSource.CreateLinkedTokenSource(token);
                var buffer = new byte[128 * 1024];
                long received = 0;
                while (true)
                {
                    stalled.CancelAfter(Stall);
                    int read;
                    try { read = await input.ReadAsync(buffer, stalled.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new LinkException(LinkMessages.Interrupted); }
                    if (read == 0) break;
                    received += read;
                    if (received > options.Limit) throw new LinkException(LinkMessages.TooLarge);
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    if (total > 0) percent?.Report(Math.Min(100, received * 100.0 / total.Value));
                }
                if (total > 0 && received != total) throw new LinkException(LinkMessages.Interrupted);
                if (received == 0) throw new LinkException(LinkMessages.NoSound);
            }
            File.Move(partial, destination);
            percent?.Report(100);
            return new FetchedLink(destination, Path.GetFileNameWithoutExtension(destination));
        }
        finally { try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>A name that is safe to create: what the site suggested or the last part of the address, without path pieces, characters Windows does not allow or an over-long stem.</summary>
    internal static string FileNameFor(HttpResponseMessage response, Uri address)
    {
        var suggested = SuggestedName(response);
        if (string.IsNullOrWhiteSpace(suggested))
        {
            var last = address.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
            try { suggested = Uri.UnescapeDataString(last); } catch (UriFormatException) { suggested = last; }
        }
        var stem = new StringBuilder();
        foreach (var c in Path.GetFileNameWithoutExtension(suggested ?? ""))
            if (!Path.GetInvalidFileNameChars().Contains(c) && !char.IsControl(c)) stem.Append(c);
        var clean = stem.ToString().Trim(' ', '.');
        if (clean.Length > 100) clean = clean[..100].TrimEnd(' ', '.');
        if (clean.Length == 0 || IsReservedName(clean)) clean = "link";
        var extension = Path.GetExtension(suggested ?? "");
        if (!MediaExtensions.Contains(extension)) extension = ExtensionFor(response.Content.Headers.ContentType?.MediaType);
        return clean + extension;
    }

    private static bool IsReservedName(string stem) =>
        stem.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL" or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9"
            or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9";

    private static string ExtensionFor(string? type) => type?.ToLowerInvariant() switch
    {
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
        "audio/mp4" or "audio/x-m4a" => ".m4a",
        "audio/aac" => ".aac",
        "audio/flac" or "audio/x-flac" => ".flac",
        "audio/ogg" or "application/ogg" => ".ogg",
        "audio/opus" => ".opus",
        "audio/webm" => ".weba",
        "video/mp4" => ".mp4",
        "video/webm" => ".webm",
        "video/quicktime" => ".mov",
        "video/x-matroska" => ".mkv",
        _ => ".bin"
    };

    private static string Unique(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
        return path;
    }

    public void Dispose()
    {
        if (!_owns) return;
        _any.Dispose();
        _public.Dispose();
    }
}
