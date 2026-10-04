using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Authentication;
using System.Text.Json;
using TriAsr.Application;
using TriAsr.Domain;

namespace TriAsr.Infrastructure;

/// <summary>What a first look at a server shows, before anything secret is sent to it.</summary>
/// <param name="Fingerprint">The certificate the server presented, or null for a plain http server.</param>
public sealed record RemoteProbe(RemoteHealth Health, string? Fingerprint, Uri Address);

/// <summary>
/// Talks to a Mockingbird server (Studio hosting or the Server edition). An https server is trusted by its fingerprint alone: the one the user
/// confirmed is passed in, and a server that presents another is refused with <see cref="RemoteException.IsIdentityChanged"/>. The password is sent
/// only by a client that has such a fingerprint, so it never goes to a server nobody has confirmed. Everything here is plain HTTP calls; no model runs.
/// </summary>
public sealed class RemoteServerClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly string? _pinned;

    public Uri Address { get; }

    /// <param name="pinnedFingerprint">Required for https. Ignored for http.</param>
    public RemoteServerClient(Uri address, string? password, string? pinnedFingerprint)
    {
        Address = address; _pinned = pinnedFingerprint;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(1), UseProxy = false,
            SslOptions = { RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null && ServerIdentity.Same(ServerIdentity.FingerprintOf(certificate), _pinned) }
        };
        _http = new HttpClient(handler, true) { BaseAddress = address, Timeout = Timeout.InfiniteTimeSpan };
        if (!string.IsNullOrEmpty(password)) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", password);
    }

    /// <summary>
    /// Asks <c>/v1/health</c> with no credentials, accepting whatever certificate the server shows, and returns what it said and the fingerprint it showed.
    /// Nothing that was typed or saved is sent, so asking an unknown server costs nothing.
    /// </summary>
    public static async Task<RemoteProbe> ProbeAsync(Uri address, CancellationToken token)
    {
        string? fingerprint = null;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(8), UseProxy = false,
            SslOptions = { RemoteCertificateValidationCallback = (_, certificate, _, _) => { if (certificate is not null) fingerprint = ServerIdentity.FingerprintOf(certificate); return certificate is not null; } }
        };
        using var http = new HttpClient(handler, true) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(12) };
        try
        {
            using var response = await http.GetAsync("/v1/health", token);
            if (!response.IsSuccessStatusCode) throw new RemoteException((int)response.StatusCode, "not_a_server", "That address answered, but not like a Mockingbird server.");
            RemoteHealth? health;
            try { health = await response.Content.ReadJsonAsync<RemoteHealth>(token); }
            catch (JsonException) { health = null; }
            if (health is not { Status: "ok" }) throw new RemoteException(200, "not_a_server", "That address answered, but not like a Mockingbird server.");
            return new RemoteProbe(health, fingerprint, address);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or AuthenticationException)
        {
            if (token.IsCancellationRequested) throw;
            throw new RemoteException(0, "unreachable", "The server could not be reached: " + (error.InnerException?.Message ?? error.Message), error);
        }
    }

    // ---- calls ------------------------------------------------------------------------------------------------------------------------

    private async Task<T> GetAsync<T>(string path, CancellationToken token, int seconds = 30)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, path), seconds, token);
        return (await response.Content.ReadJsonAsync<T>(token))!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, int seconds, CancellationToken token, HttpCompletionOption completion = HttpCompletionOption.ResponseHeadersRead)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (seconds > 0) timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, completion, timeout.Token); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
        {
            if (token.IsCancellationRequested) throw;
            if (error.InnerException is AuthenticationException or IOException { InnerException: AuthenticationException })
                throw new RemoteException(0, "identity_changed", "The server presented a different identity than the one you trusted. If you did not expect that, do not connect.", error);
            throw new RemoteException(0, "unreachable", "The server could not be reached: " + (error.InnerException?.Message ?? error.Message), error);
        }
        if (response.IsSuccessStatusCode) return response;
        using (response) throw await ErrorOfAsync(response, token);
    }

    private static async Task<RemoteException> ErrorOfAsync(HttpResponseMessage response, CancellationToken token)
    {
        string code = "error", message = $"The server answered {(int)response.StatusCode}.";
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("code", out var codeValue) && codeValue.GetString() is { } c) code = c;
                if (error.TryGetProperty("message", out var messageValue) && messageValue.GetString() is { } m) message = m;
            }
        }
        catch (JsonException) { }
        return new RemoteException((int)response.StatusCode, code, message);
    }

    public Task<RemoteServerInfo> InfoAsync(CancellationToken token) => GetAsync<RemoteServerInfo>("/v1/server", token);

    public async Task<IReadOnlyList<RemoteLanguage>> LanguagesAsync(CancellationToken token) => (await GetAsync<RemoteLanguages>("/v1/languages", token)).Data;

    public async Task<IReadOnlyList<RemoteJob>> ListAsync(CancellationToken token) => (await GetAsync<RemoteJobs>("/v1/transcriptions", token)).Data;

    /// <param name="waitSeconds">Hold the answer until the job ends, up to this long.</param>
    public Task<RemoteJob> GetAsync(Guid id, CancellationToken token, int waitSeconds = 0) =>
        GetAsync<RemoteJob>($"/v1/transcriptions/{id}" + (waitSeconds > 0 ? $"?wait={waitSeconds}" : ""), token, waitSeconds > 0 ? waitSeconds + 15 : 30);

    public async Task<RemoteJob> CancelAsync(Guid id, CancellationToken token)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/v1/transcriptions/{id}/cancel"), 30, token);
        return (await response.Content.ReadJsonAsync<RemoteJob>(token))!;
    }

    /// <summary>Has the server read one phrase of live dictation (a WAV file of a few seconds) and returns the words and the language they are in.</summary>
    /// <param name="recent">The language of the previous phrase, for a choice of two languages; null when there is none.</param>
    public async Task<LivePhrase> LiveAsync(byte[] wav, string language, string? recent, CancellationToken token)
    {
        using var content = new ByteArrayContent(wav);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        var query = $"/v1/live?language={Uri.EscapeDataString(language)}" + (string.IsNullOrEmpty(recent) ? "" : $"&recent={Uri.EscapeDataString(recent)}");
        using var request = new HttpRequestMessage(HttpMethod.Post, query) { Content = content };
        using var response = await SendAsync(request, 120, token);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = document.RootElement;
        return new(root.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "", root.TryGetProperty("language", out var spoken) ? spoken.GetString() ?? "" : "");
    }

    // ---- notes ------------------------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<RemoteNoteSummary>> NotesAsync(CancellationToken token) => (await GetAsync<RemoteNotes>("/v1/notes", token)).Data;

    public Task<RemoteNote> NoteAsync(Guid id, CancellationToken token) => GetAsync<RemoteNote>($"/v1/notes/{id}", token);

    public async Task<RemoteNote> CreateNoteAsync(string title, string text, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/notes") { Content = JsonContent.Create(new RemoteNoteEdit(title, text, null), options: Json) };
        using var response = await SendAsync(request, 60, token);
        return (await response.Content.ReadJsonAsync<RemoteNote>(token))!;
    }

    /// <summary>Saves a note that was open at <paramref name="revision"/>. A note that someone else saved meanwhile is refused (<c>note_changed</c>); one that is gone, too (<c>not_found</c>).</summary>
    public async Task<RemoteNote> SaveNoteAsync(Guid id, string title, string text, int revision, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/v1/notes/{id}") { Content = JsonContent.Create(new RemoteNoteEdit(title, text, revision), options: Json) };
        using var response = await SendAsync(request, 60, token);
        return (await response.Content.ReadJsonAsync<RemoteNote>(token))!;
    }

    public async Task DeleteNoteAsync(Guid id, CancellationToken token)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/v1/notes/{id}"), 60, token);
    }
    /// <summary>Deletes a finished recording on the server with its transcript, edits and the copy of the recording it holds. One that is still being worked on is refused (<c>still_running</c>).</summary>
    public async Task DeleteAsync(Guid id, CancellationToken token)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/v1/transcriptions/{id}"), 60, token);
    }

    /// <summary>Sends a recording. The file goes from disk to the connection in pieces, with the count of bytes sent reported as it goes.</summary>
    /// <param name="speakers"><c>off</c>, <c>auto</c> or how many speakers there are; sent only when it is not <c>off</c>, so an older server is asked nothing new.</param>
    public async Task<RemoteJob> UploadAsync(string path, string language, IProgress<long>? progress, CancellationToken token, string speakers = "off")
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var content = new StreamContent(new ProgressStream(file, progress), 1024 * 1024);
        content.Headers.ContentLength = file.Length;
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var asked = speakers is "off" or "" ? "" : $"&speakers={Uri.EscapeDataString(speakers)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/transcriptions?language={Uri.EscapeDataString(language)}&name={Uri.EscapeDataString(Path.GetFileName(path))}{asked}") { Content = content };
        using var response = await SendAsync(request, 0, token);
        return (await response.Content.ReadJsonAsync<RemoteJob>(token))!;
    }

    /// <summary>Asks the server to fetch the sound of a web address and transcribe it. The server downloads it; nothing is downloaded here.</summary>
    public async Task<RemoteJob> SendLinkAsync(string url, string language, CancellationToken token, string speakers = "off")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/links") { Content = JsonContent.Create(new RemoteLinkRequest(url, language, speakers is "off" or "" ? null : speakers), options: Json) };
        using var response = await SendAsync(request, 60, token);
        return (await response.Content.ReadJsonAsync<RemoteJob>(token))!;
    }

    public Task<RemoteReview> ReviewAsync(Guid id, CancellationToken token) => GetAsync<RemoteReview>($"/v1/transcriptions/{id}/review", token);

    /// <summary>The summary of a finished transcript, or how far it is.</summary>
    public Task<RemoteSummary> SummaryAsync(Guid id, CancellationToken token) => GetAsync<RemoteSummary>($"/v1/transcriptions/{id}/summary", token);

    /// <summary>Asks the server to write (or write again) the summary of a finished transcript; ask <see cref="SummaryAsync"/> until it is done.</summary>
    public async Task<RemoteSummary> StartSummaryAsync(Guid id, CancellationToken token)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/v1/transcriptions/{id}/summary"), 60, token);
        return (await response.Content.ReadJsonAsync<RemoteSummary>(token))!;
    }

    /// <summary>Every transcript on the server searched at once; ask only a server whose <see cref="RemoteServerInfo.Search"/> is true.</summary>
    public Task<RemoteSearch> SearchAsync(string query, CancellationToken token) => GetAsync<RemoteSearch>($"/v1/search?q={Uri.EscapeDataString(query)}", token);

    /// <param name="speakerNames">The names of the speakers as they now stand (empty to take them all away); null leaves them as they are.</param>
    public async Task SaveEditsAsync(Guid id, IReadOnlyList<RemoteEdit> edits, CancellationToken token, IReadOnlyDictionary<string, string>? speakerNames = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/v1/transcriptions/{id}/review") { Content = JsonContent.Create(new RemoteEdits(edits, speakerNames), options: Json) };
        using var response = await SendAsync(request, 60, token);
    }

    /// <summary>The transcript in a file format, as the server writes it.</summary>
    public async Task<byte[]> ExportAsync(Guid id, string format, string mode, CancellationToken token)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/v1/transcriptions/{id}/transcript?format={Uri.EscapeDataString(format)}&mode={Uri.EscapeDataString(mode)}"), 60, token);
        return await response.Content.ReadAsByteArrayAsync(token);
    }

    /// <summary>Downloads the audio of a finished job (<c>playback</c>, a listening copy, or <c>normalized</c>, the 16 kHz file) to a file.</summary>
    public async Task DownloadAudioAsync(Guid id, string kind, string destination, IProgress<long>? progress, CancellationToken token)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/v1/transcriptions/{id}/audio?kind={Uri.EscapeDataString(kind)}"), 0, token);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var temporary = destination + ".part";
        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
            {
                var buffer = new byte[256 * 1024]; long total = 0; int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0) { await file.WriteAsync(buffer.AsMemory(0, read), token); progress?.Report(total += read); }
            }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Reports how much of a file has been read, which for an upload is how much has been sent.</summary>
    private sealed class ProgressStream(Stream inner, IProgress<long>? progress) : Stream
    {
        private long _total;
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { var read = inner.Read(buffer, offset, count); progress?.Report(_total += read); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { var read = await inner.ReadAsync(buffer, cancellationToken); progress?.Report(_total += read); return read; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal static class RemoteJsonExtensions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Reads a JSON answer with the API's naming (camelCase).</summary>
    public static Task<T?> ReadJsonAsync<T>(this HttpContent content, CancellationToken token) => content.ReadFromJsonAsync<T>(Json, token);
}
