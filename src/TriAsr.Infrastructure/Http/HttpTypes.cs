using System.Net;
using System.Text;
using System.Text.Json;

namespace TriAsr.Infrastructure;

/// <summary>A request that cannot be served. The server turns it into an HTTP error (and, before any handler runs, ends the request).</summary>
public sealed class HttpProtocolException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>One request, parsed. The body is a stream that has not been read yet: a handler that refuses the request never pays for the upload.</summary>
public sealed class HttpRequest
{
    public required string Method { get; init; }
    /// <summary>The path without the query, percent-decoded.</summary>
    public required string Path { get; init; }
    public required IReadOnlyDictionary<string, string> Query { get; init; }
    /// <summary>Header names are matched without regard to case.</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    public long? ContentLength { get; init; }
    public bool Chunked { get; init; }
    public IPEndPoint? Remote { get; init; }
    /// <summary>True when the request arrived over TLS (https).</summary>
    public bool IsSecure { get; init; }
    /// <summary>The request body. The first read tells a client that waits for "100 Continue" to go ahead.</summary>
    public required Stream Body { get; init; }

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

public sealed class HttpResponse
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public int Status { get; init; } = 200;
    public string ContentType { get; init; } = "application/json; charset=utf-8";
    public byte[] Body { get; init; } = [];
    /// <summary>A file sent as the body, read from disk while it is written (so a long recording is never held in memory).</summary>
    public string? FilePath { get; init; }
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static HttpResponse Json(int status, object value) => new() { Status = status, Body = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions) };

    public static HttpResponse Text(int status, string text, string contentType = "text/plain; charset=utf-8") =>
        new() { Status = status, ContentType = contentType, Body = new UTF8Encoding(false).GetBytes(text) };

    public static HttpResponse Bytes(int status, byte[] bytes, string contentType) => new() { Status = status, ContentType = contentType, Body = bytes };

    public static HttpResponse File(string path, string contentType) => new() { ContentType = contentType, FilePath = path };

    public static HttpResponse Empty(int status) => new() { Status = status, ContentType = "text/plain; charset=utf-8" };

    /// <summary>The error shape of the API, which is also what OpenAI clients read: <c>{"error":{"code","message","type"}}</c>. The code is for programs, the message for people.</summary>
    public static HttpResponse Error(int status, string code, string message) =>
        Json(status, new { error = new { code, message, type = status < 500 ? "invalid_request_error" : "server_error" } });

    public HttpResponse With(string name, string value) { Headers[name] = value; return this; }
}

public delegate Task<HttpResponse> HttpHandler(HttpRequest request, CancellationToken token);

/// <param name="Address">Where to listen: loopback to stay on this computer, or any address to serve the network.</param>
/// <param name="Port">0 picks a free port (used by tests).</param>
/// <param name="MaxBodyBytes">Largest request body accepted; a larger Content-Length is refused before a byte of it is read.</param>
/// <param name="MaxConnections">Connections served at once; more are answered with 503 and closed.</param>
/// <param name="HeaderTimeout">Time a client has to send the request line and headers (defends against slow connections).</param>
/// <param name="BodyIdleTimeout">Longest pause between two pieces of the body.</param>
/// <param name="MaxHeaderBytes">Largest request line plus headers.</param>
/// <param name="Certificate">With a certificate the server speaks TLS (https) on the same port; without one it speaks plain HTTP only.</param>
/// <param name="AllowPlain">With a certificate: whether a connection that does not start a TLS handshake is still served (plain HTTP). Off, it is told to use https.</param>
public sealed record HttpServerOptions(IPAddress Address, int Port, long MaxBodyBytes = 8L * 1024 * 1024 * 1024, int MaxConnections = 32,
    TimeSpan? HeaderTimeout = null, TimeSpan? BodyIdleTimeout = null, int MaxHeaderBytes = 16 * 1024,
    System.Security.Cryptography.X509Certificates.X509Certificate2? Certificate = null, bool AllowPlain = true)
{
    public TimeSpan HeaderTimeoutOrDefault => HeaderTimeout ?? TimeSpan.FromSeconds(15);
    public TimeSpan BodyIdleTimeoutOrDefault => BodyIdleTimeout ?? TimeSpan.FromSeconds(60);
}
