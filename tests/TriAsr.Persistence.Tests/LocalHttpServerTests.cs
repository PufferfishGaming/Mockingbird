using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using TriAsr.Infrastructure;

namespace TriAsr.Persistence.Tests;

public sealed class LocalHttpServerTests
{
    private static HttpServerOptions Options(long maxBody = 64L * 1024 * 1024, int maxConnections = 32, int headerMs = 5000, int idleMs = 5000, int maxHeaders = 16 * 1024) =>
        new(IPAddress.Loopback, 0, maxBody, maxConnections, TimeSpan.FromMilliseconds(headerMs), TimeSpan.FromMilliseconds(idleMs), maxHeaders);

    private static async Task<string> SendAsync(LocalHttpServer server, string raw, byte[]? body = null, bool shutdownSend = false)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.Latin1.GetBytes(raw));
        if (body is not null) await stream.WriteAsync(body);
        if (shutdownSend) client.Client.Shutdown(SocketShutdown.Send);
        return await ReadAllAsync(stream);
    }

    private static async Task<string> ReadAllAsync(Stream stream)
    {
        using var memory = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await stream.CopyToAsync(memory, timeout.Token); } catch (IOException) { }
        return Encoding.Latin1.GetString(memory.ToArray());
    }

    private static string Body(string response) => response[(response.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];

    private static LocalHttpServer Start(HttpServerOptions options, HttpHandler handler, Action<Exception>? onError = null)
    {
        var server = new LocalHttpServer(options, handler, onError);
        server.Start();
        return server;
    }

    [Fact]
    public async Task AnAnswerCarriesItsLengthAndNothingThatNamesTheServer()
    {
        await using var server = Start(Options(), (request, _) => Task.FromResult(HttpResponse.Json(200, new { path = request.Path, a = request.Query["a"], q = request.Query["q"] })));
        var response = await SendAsync(server, "GET /v1/some%20thing?a=1&q=x%20y+z HTTP/1.1\r\nHost: localhost\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", response);
        Assert.Contains("Connection: close", response);
        Assert.Contains("X-Content-Type-Options: nosniff", response);
        Assert.Contains("Cache-Control: no-store", response);
        Assert.DoesNotContain("Server:", response);
        Assert.Contains("\"path\":\"/v1/some thing\"", response);
        Assert.Contains("\"a\":\"1\"", response);
        Assert.Contains("\"q\":\"x y z\"", response);
        var length = int.Parse(response.Split("Content-Length: ")[1].Split("\r\n")[0]);
        Assert.Equal(length, Encoding.Latin1.GetByteCount(Body(response)));
    }

    [Fact]
    public async Task AnUploadOfTwentyMegabytesIsReadInPiecesAndArrivesIntact()
    {
        var data = RandomNumberGenerator.GetBytes(20 * 1024 * 1024);
        await using var server = Start(Options(), async (request, token) =>
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024]; long total = 0; int read;
            while ((read = await request.Body.ReadAsync(buffer, token)) > 0) { hash.AppendData(buffer, 0, read); total += read; }
            return HttpResponse.Json(200, new { total, sha = Convert.ToHexString(hash.GetHashAndReset()) });
        });
        var response = await SendAsync(server, $"POST /upload HTTP/1.1\r\nHost: x\r\nContent-Length: {data.Length}\r\n\r\n", data);
        Assert.Contains($"\"total\":{data.Length}", response);
        Assert.Contains(Convert.ToHexString(SHA256.HashData(data)), response);
    }

    [Fact]
    public async Task AChunkedUploadIsDecodedAndTheTrailerIsIgnored()
    {
        await using var server = Start(Options(), async (request, token) =>
        {
            using var memory = new MemoryStream();
            await request.Body.CopyToAsync(memory, token);
            return HttpResponse.Text(200, Encoding.UTF8.GetString(memory.ToArray()));
        });
        var response = await SendAsync(server, "POST /x HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nHello\r\n7;ext=1\r\n, world\r\n0\r\nX-Trailer: 1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200", response);
        Assert.Equal("Hello, world", Body(response));
    }

    [Fact]
    public async Task ABrokenChunkIsRefused()
    {
        await using var server = Start(Options(), async (request, token) => { await request.Body.CopyToAsync(Stream.Null, token); return HttpResponse.Empty(200); });
        Assert.StartsWith("HTTP/1.1 400", await SendAsync(server, "POST /x HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\nZZ\r\nHello\r\n0\r\n\r\n"));
        Assert.StartsWith("HTTP/1.1 400", await SendAsync(server, "POST /x HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nHelloXX0\r\n\r\n"));
    }

    [Fact]
    public async Task AnUploadLargerThanTheLimitIsRefusedBeforeItIsSent()
    {
        var reads = 0;
        await using var server = Start(Options(maxBody: 1024 * 1024), (request, _) => { reads++; return Task.FromResult(HttpResponse.Empty(200)); });
        var response = await SendAsync(server, "POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: 10737418240\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 413", response);
        Assert.Contains("payload_too_large", response);
        Assert.Equal(0, reads); // the handler never saw the request
    }

    [Fact]
    public async Task AChunkedUploadThatGrowsPastTheLimitIsStopped()
    {
        await using var server = Start(Options(maxBody: 1000), async (request, token) => { await request.Body.CopyToAsync(Stream.Null, token); return HttpResponse.Empty(200); });
        var chunk = new string('a', 600);
        var response = await SendAsync(server, $"POST /x HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n258\r\n{chunk}\r\n258\r\n{chunk}\r\n0\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 413", response);
    }

    [Theory]
    [InlineData("POST /x HTTP/1.1\r\nHost: x\r\n\r\n", "HTTP/1.1 411")]                                                                       // a body without a length
    [InlineData("POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: 3\r\nTransfer-Encoding: chunked\r\n\r\n", "HTTP/1.1 400")]               // two ways to say where the body ends
    [InlineData("POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: 3\r\nContent-Length: 4\r\n\r\nabc", "HTTP/1.1 400")]                     // two lengths
    [InlineData("POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: -1\r\n\r\n", "HTTP/1.1 400")]
    [InlineData("POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: 0x10\r\n\r\n", "HTTP/1.1 400")]
    [InlineData("POST /x HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: gzip\r\n\r\n", "HTTP/1.1 501")]
    [InlineData("GET /x HTTP/2.0\r\nHost: x\r\n\r\n", "HTTP/1.1 505")]
    [InlineData("GET /x\r\nHost: x\r\n\r\n", "HTTP/1.1 400")]
    [InlineData("get /x HTTP/1.1\r\nHost: x\r\n\r\n", "HTTP/1.1 400")]
    [InlineData("GET x HTTP/1.1\r\nHost: x\r\n\r\n", "HTTP/1.1 400")]
    [InlineData("GET /x HTTP/1.1\r\nBad Header: x\r\n\r\n", "HTTP/1.1 400")]
    [InlineData("GET /x HTTP/1.1\r\n folded: x\r\n\r\n", "HTTP/1.1 400")]
    [InlineData("GET /x HTTP/1.1\r\nHost: x\r\nHost: y\r\n\r\n", "HTTP/1.1 400")]
    [InlineData("GET /x HTTP/1.1\r\nHost: x\n\r\n", "HTTP/1.1 400")]                                                                        // a bare line feed
    public async Task AMalformedRequestIsAnsweredWithAnErrorAndNeverReachesTheHandler(string raw, string expected)
    {
        var reached = false;
        await using var server = Start(Options(), (_, _) => { reached = true; return Task.FromResult(HttpResponse.Empty(200)); });
        var response = await SendAsync(server, raw, shutdownSend: true);
        Assert.StartsWith(expected, response);
        Assert.False(reached);
    }

    [Fact]
    public async Task HeadersLargerThanTheLimitAreRefused()
    {
        await using var server = Start(Options(maxHeaders: 2048), (_, _) => Task.FromResult(HttpResponse.Empty(200)));
        var response = await SendAsync(server, "GET /x HTTP/1.1\r\nHost: x\r\nX-Big: " + new string('a', 6000) + "\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 431", response);
        var many = string.Concat(Enumerable.Range(0, 200).Select(i => $"X-{i}: v\r\n"));
        await using var other = Start(Options(), (_, _) => Task.FromResult(HttpResponse.Empty(200)));
        Assert.StartsWith("HTTP/1.1 431", await SendAsync(other, "GET /x HTTP/1.1\r\nHost: x\r\n" + many + "\r\n"));
    }

    [Fact]
    public async Task AClientThatNeverFinishesItsHeadersIsLetGo()
    {
        await using var server = Start(Options(headerMs: 300), (_, _) => Task.FromResult(HttpResponse.Empty(200)));
        var response = await SendAsync(server, "GET /x HTTP/1.1\r\nHost: x\r\n"); // no blank line, and the connection stays open
        Assert.StartsWith("HTTP/1.1 408", response);
    }

    [Fact]
    public async Task AnUploadThatStallsIsStopped()
    {
        await using var server = Start(Options(idleMs: 300), async (request, token) => { await request.Body.CopyToAsync(Stream.Null, token); return HttpResponse.Empty(200); });
        var response = await SendAsync(server, "POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: 1000\r\n\r\nabc"); // 997 bytes never come
        Assert.StartsWith("HTTP/1.1 408", response);
    }

    [Fact]
    public async Task ABodyShorterThanItsLengthIsAnError()
    {
        await using var server = Start(Options(), async (request, token) => { await request.Body.CopyToAsync(Stream.Null, token); return HttpResponse.Empty(200); });
        Assert.StartsWith("HTTP/1.1 400", await SendAsync(server, "POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: 1000\r\n\r\nabc", shutdownSend: true));
    }

    [Fact]
    public async Task AnExceptionInAHandlerIsAGenericErrorThatLeaksNothing()
    {
        Exception? seen = null;
        await using var server = Start(Options(), (_, _) => throw new InvalidOperationException(@"secret C:\Users\Anna\file.wav"), error => seen = error);
        var response = await SendAsync(server, "GET /x HTTP/1.1\r\nHost: x\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 500", response);
        Assert.DoesNotContain("secret", response);
        Assert.DoesNotContain("Anna", response);
        Assert.Contains("internal_error", response);
        Assert.NotNull(seen);
    }

    [Fact]
    public async Task ARefusedUploadStillGetsItsAnswer()
    {
        // The handler answers 401 without reading a byte; the client keeps sending and must read the answer, not a reset.
        await using var server = Start(Options(), (_, _) => Task.FromResult(HttpResponse.Error(401, "unauthorized", "no")));
        var response = await SendAsync(server, "POST /x HTTP/1.1\r\nHost: x\r\nContent-Length: 300000\r\n\r\n", new byte[300000]);
        Assert.StartsWith("HTTP/1.1 401", response);
    }

    [Fact]
    public async Task ExpectContinueIsAnsweredOnlyWhenTheHandlerReadsTheBody()
    {
        await using var server = Start(Options(), async (request, token) =>
        {
            if (request.Path == "/refuse") return HttpResponse.Error(401, "unauthorized", "no");
            await request.Body.CopyToAsync(Stream.Null, token);
            return HttpResponse.Empty(200);
        });
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.Latin1.GetBytes("POST /accept HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\nExpect: 100-continue\r\n\r\n"));
        var first = new byte[64];
        var count = await stream.ReadAsync(first);
        Assert.StartsWith("HTTP/1.1 100 Continue", Encoding.Latin1.GetString(first, 0, count));
        await stream.WriteAsync("hello"u8.ToArray());
        Assert.StartsWith("HTTP/1.1 200", await ReadAllAsync(stream));

        var refused = await SendAsync(server, "POST /refuse HTTP/1.1\r\nHost: x\r\nContent-Length: 5\r\nExpect: 100-continue\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 401", refused);   // no 100 Continue first: the client does not upload
        Assert.DoesNotContain("100 Continue", refused);
    }

    [Fact]
    public async Task MoreConnectionsThanTheLimitAreTurnedAway()
    {
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        await using var server = Start(Options(maxConnections: 1), async (request, _) => { started.TrySetResult(); await release.Task; return HttpResponse.Empty(200); });
        var first = SendAsync(server, "GET /x HTTP/1.1\r\nHost: x\r\n\r\n");
        await started.Task;
        var second = await SendAsync(server, "GET /x HTTP/1.1\r\nHost: x\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 503", second);
        release.SetResult();
        Assert.StartsWith("HTTP/1.1 200", await first);
    }

    [Fact]
    public async Task OnlyOneServerCanHoldAPort()
    {
        await using var first = Start(Options(), (_, _) => Task.FromResult(HttpResponse.Empty(200)));
        var second = new LocalHttpServer(new HttpServerOptions(IPAddress.Loopback, first.Port), (_, _) => Task.FromResult(HttpResponse.Empty(200)));
        Assert.Throws<SocketException>(() => second.Start());
        await second.DisposeAsync();
    }

    [Fact]
    public async Task AStoppedServerNoLongerAnswers()
    {
        var server = Start(Options(), (_, _) => Task.FromResult(HttpResponse.Empty(200)));
        var port = server.Port;
        await server.DisposeAsync();
        using var client = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port));
    }

    [Fact]
    public async Task ANetworkServerListensOnEveryAddressAndALoopbackServerOnlyOnTheLoopback()
    {
        await using var loopback = Start(Options(), (_, _) => Task.FromResult(HttpResponse.Empty(200)));
        Assert.Equal(IPAddress.Loopback, loopback.Address);
        var any = Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any;
        await using var network = Start(new HttpServerOptions(any, 0), (_, _) => Task.FromResult(HttpResponse.Empty(200)));
        Assert.True(network.Address.Equals(IPAddress.Any) || network.Address.Equals(IPAddress.IPv6Any));
        Assert.StartsWith("HTTP/1.1 200", await SendAsync(network, "GET /x HTTP/1.1\r\nHost: x\r\n\r\n"));
    }
}
