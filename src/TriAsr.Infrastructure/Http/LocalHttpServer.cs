using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TriAsr.Infrastructure;

/// <summary>
/// A small HTTP/1.1 server for the app's own API, written on plain sockets so that it listens on any address without administrator rights or a
/// URL reservation, and without a package. It does only what the API needs: one request per connection (<c>Connection: close</c>), request bodies
/// of a known length or chunked, "Expect: 100-continue", and strict limits on everything a client controls (header size and time, body size and
/// pauses, number of connections). It never maps a path to a file; the handler decides everything.
/// </summary>
public sealed class LocalHttpServer : IAsyncDisposable
{
    private readonly HttpServerOptions _options;
    private readonly HttpHandler _handler;
    private readonly Action<Exception>? _onHandlerError;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private Task? _accepting;

    public LocalHttpServer(HttpServerOptions options, HttpHandler handler, Action<Exception>? onHandlerError = null)
    {
        _options = options; _handler = handler; _onHandlerError = onHandlerError;
        _slots = new SemaphoreSlim(Math.Max(1, options.MaxConnections));
        _listener = new TcpListener(options.Address, options.Port) { ExclusiveAddressUse = true };
        if (options.Address.AddressFamily == AddressFamily.InterNetworkV6) _listener.Server.DualMode = true;
    }

    /// <summary>The port in use (the real one when 0 was asked for).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The address listened on: loopback only, or every address.</summary>
    public IPAddress Address => ((IPEndPoint)_listener.LocalEndpoint).Address;

    /// <summary>Starts listening. Throws a <see cref="SocketException"/> when the port is taken or not allowed.</summary>
    public void Start()
    {
        _listener.Start(backlog: 64);
        _accepting = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { if (_stop.IsCancellationRequested) return; continue; }
            if (!await _slots.WaitAsync(0))
            {
                _ = Task.Run(() => RefuseAsync(client));
                continue;
            }
            Task? connection = null;
            connection = Task.Run(async () =>
            {
                try { await HandleAsync(client); }
                finally { _slots.Release(); if (connection is not null) _connections.TryRemove(connection, out _); }
            });
            _connections[connection] = 0;
            if (connection.IsCompleted) _connections.TryRemove(connection, out _); // finished before it was listed
        }
    }

    private static async Task RefuseAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await WriteResponseAsync(client.GetStream(), HttpResponse.Error(503, "busy", "The server is serving as many connections as it allows. Try again in a moment."), false, timeout.Token);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                HttpRequest? request;
                RequestBodyStream? body;
                try
                {
                    (request, body) = await ReadRequestAsync(stream, client);
                }
                catch (HttpProtocolException error)
                {
                    await WriteResponseAsync(stream, HttpResponse.Error(error.Status, error.Code, error.Message), false, _stop.Token);
                    return;
                }
                if (request is null || body is null) return; // the client went away before it said anything

                HttpResponse response;
                try { response = await _handler(request, _stop.Token); }
                catch (HttpProtocolException error) { response = HttpResponse.Error(error.Status, error.Code, error.Message); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    _onHandlerError?.Invoke(error);
                    response = HttpResponse.Error(500, "internal_error", "The server could not handle the request.");
                }
                // Let an upload that was refused finish arriving (up to a limit), so that the client can read the answer instead of a reset.
                if (!body.IsComplete && !body.AwaitingContinue) await body.DrainAsync(1024 * 1024, TimeSpan.FromSeconds(2));
                await WriteResponseAsync(stream, response, request.Method == "HEAD", _stop.Token);
                try { client.Client.Shutdown(SocketShutdown.Send); } catch (SocketException) { }
            }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        }
    }

    private async Task<(HttpRequest?, RequestBodyStream?)> ReadRequestAsync(NetworkStream stream, TcpClient client)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(_options.HeaderTimeoutOrDefault);
        byte[] head; byte[] leftover;
        try
        {
            var buffer = new byte[_options.MaxHeaderBytes + 1024];
            var count = 0; var scanned = 0; var end = -1;
            while (end < 0)
            {
                if (count == buffer.Length) throw new HttpProtocolException(431, "headers_too_large", "The request headers are too large.");
                var read = await stream.ReadAsync(buffer.AsMemory(count), timeout.Token);
                if (read == 0)
                {
                    if (count == 0) return (null, null);
                    throw new HttpProtocolException(400, "bad_request", "The request ended before its headers did.");
                }
                count += read;
                for (var i = Math.Max(0, scanned - 3); i + 3 < count; i++)
                    if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n') { end = i; break; }
                scanned = count;
                if (end > _options.MaxHeaderBytes || end < 0 && count > _options.MaxHeaderBytes)
                    throw new HttpProtocolException(431, "headers_too_large", "The request headers are too large.");
            }
            head = buffer[..end];
            leftover = buffer[(end + 4)..count];
        }
        catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
        {
            throw new HttpProtocolException(408, "request_timeout", "The request headers did not arrive in time.");
        }

        var lines = Encoding.Latin1.GetString(head).Split("\r\n");
        if (lines.Any(line => line.Contains('\r') || line.Contains('\n') || line.Contains('\0'))) throw new HttpProtocolException(400, "bad_request", "The request contains an invalid character.");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || requestLine[0].Length == 0 || !requestLine[0].All(char.IsAsciiLetterUpper))
            throw new HttpProtocolException(400, "bad_request", "The request line is not valid.");
        if (requestLine[2] is not ("HTTP/1.1" or "HTTP/1.0")) throw new HttpProtocolException(505, "http_version", "Only HTTP/1.0 and HTTP/1.1 are supported.");
        var target = requestLine[1];
        if (!target.StartsWith('/')) throw new HttpProtocolException(400, "bad_request", "The request target must be a path.");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || line[0] is ' ' or '\t') throw new HttpProtocolException(400, "bad_request", "A header line is not valid.");
            var name = line[..colon];
            if (name.Any(c => c <= ' ' || c >= 127 || c == ':')) throw new HttpProtocolException(400, "bad_request", "A header name is not valid.");
            var value = line[(colon + 1)..].Trim(' ', '\t');
            if (headers.ContainsKey(name))
            {
                // A repeated length would let two parties disagree about where the body ends; any other repeat is joined as HTTP allows.
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) || name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                    throw new HttpProtocolException(400, "bad_request", "A header is given twice.");
                headers[name] += ", " + value;
            }
            else headers[name] = value;
            if (headers.Count > 100) throw new HttpProtocolException(431, "headers_too_large", "The request has too many headers.");
        }

        long? length = null; var chunked = false;
        if (headers.TryGetValue("Transfer-Encoding", out var encoding))
        {
            if (!encoding.Equals("chunked", StringComparison.OrdinalIgnoreCase)) throw new HttpProtocolException(501, "not_implemented", "Only chunked transfer encoding is supported.");
            if (headers.ContainsKey("Content-Length")) throw new HttpProtocolException(400, "bad_request", "A request cannot have both Content-Length and Transfer-Encoding.");
            chunked = true;
        }
        else if (headers.TryGetValue("Content-Length", out var text))
        {
            if (text.Length is 0 or > 18 || !text.All(char.IsAsciiDigit) || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                throw new HttpProtocolException(400, "bad_request", "Content-Length is not a number.");
            if (parsed > _options.MaxBodyBytes) throw new HttpProtocolException(413, "payload_too_large", "The upload is larger than this server accepts.");
            length = parsed;
        }
        else if (requestLine[0] is "POST" or "PUT" or "PATCH") throw new HttpProtocolException(411, "length_required", "The request needs a Content-Length (or chunked transfer encoding).");

        var path = target; var queryText = "";
        var question = target.IndexOf('?');
        if (question >= 0) { path = target[..question]; queryText = target[(question + 1)..]; }
        var reader = new ConnectionReader(stream, leftover);
        Func<CancellationToken, Task>? beforeFirstRead = null;
        if (headers.TryGetValue("Expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
            beforeFirstRead = token => stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), token).AsTask();
        var body = new RequestBodyStream(reader, length, chunked, _options.MaxBodyBytes, _options.BodyIdleTimeoutOrDefault, beforeFirstRead);
        var request = new HttpRequest
        {
            Method = requestLine[0], Path = Uri.UnescapeDataString(path), Query = ParseQuery(queryText), Headers = headers,
            ContentLength = length, Chunked = chunked, Remote = client.Client.RemoteEndPoint as IPEndPoint, Body = body
        };
        return (request, body);
    }

    private static Dictionary<string, string> ParseQuery(string text)
    {
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var name = Uri.UnescapeDataString((equals >= 0 ? pair[..equals] : pair).Replace('+', ' '));
            var value = equals >= 0 ? Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' ')) : "";
            if (name.Length > 0) query[name] = value;
        }
        return query;
    }

    private static async Task WriteResponseAsync(Stream stream, HttpResponse response, bool headOnly, CancellationToken token)
    {
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(response.Status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Reason(response.Status)).Append("\r\n");
        head.Append("Date: ").Append(DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture)).Append("\r\n");
        head.Append("Content-Type: ").Append(Clean(response.ContentType)).Append("\r\n");
        head.Append("Content-Length: ").Append(response.Body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        head.Append("Connection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n");
        foreach (var (name, value) in response.Headers) head.Append(Clean(name)).Append(": ").Append(Clean(value)).Append("\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.Latin1.GetBytes(head.ToString()), token);
        if (!headOnly && response.Body.Length > 0) await stream.WriteAsync(response.Body, token);
        await stream.FlushAsync(token);
    }

    private static string Clean(string value) => value.Contains('\r') || value.Contains('\n') ? throw new InvalidOperationException("A header contains a line break.") : value;

    private static string Reason(int status) => status switch
    {
        100 => "Continue", 200 => "OK", 201 => "Created", 202 => "Accepted", 204 => "No Content", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden",
        404 => "Not Found", 405 => "Method Not Allowed", 408 => "Request Timeout", 409 => "Conflict", 411 => "Length Required", 413 => "Payload Too Large",
        415 => "Unsupported Media Type", 422 => "Unprocessable Content", 429 => "Too Many Requests", 431 => "Request Header Fields Too Large",
        500 => "Internal Server Error", 501 => "Not Implemented", 503 => "Service Unavailable", 505 => "HTTP Version Not Supported", 507 => "Insufficient Storage",
        _ => "Status"
    };

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch (SocketException) { }
        if (_accepting is not null) { try { await _accepting.WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception error) when (error is TimeoutException or OperationCanceledException) { } }
        try { await Task.WhenAll(_connections.Keys).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception error) when (error is TimeoutException or OperationCanceledException) { }
        _stop.Dispose();
        _slots.Dispose();
    }
}
