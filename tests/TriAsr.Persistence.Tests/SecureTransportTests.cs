using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using TriAsr.Infrastructure;

namespace TriAsr.Persistence.Tests;

/// <summary>The identity of a server, the encrypted connection, and how a client recognises a server by its fingerprint (ADR-0014).</summary>
public sealed class SecureTransportTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); } catch (IOException) { } }

    private static HttpHandler Hello() => (request, _) => Task.FromResult(HttpResponse.Json(200, new { hello = "world", secure = request.IsSecure }));

    [Fact]
    public void AnIdentityIsMadeOnceKeptAndOnlyReplacedOnPurpose()
    {
        using var first = ServerIdentity.LoadOrCreate(_folder);
        Assert.True(File.Exists(Path.Combine(_folder, "server-certificate.pem")));
        Assert.True(File.Exists(Path.Combine(_folder, "server-key.pem")));
        Assert.Equal(64, first.Fingerprint.Length);
        Assert.Equal(first.Fingerprint, ServerIdentity.FingerprintOf(first.Certificate));
        using var again = ServerIdentity.LoadOrCreate(_folder);
        Assert.Equal(first.Fingerprint, again.Fingerprint);                      // the same server after a restart
        using var replaced = ServerIdentity.Create(_folder);
        Assert.NotEqual(first.Fingerprint, replaced.Fingerprint);
        using var afterwards = ServerIdentity.LoadOrCreate(_folder);
        Assert.Equal(replaced.Fingerprint, afterwards.Fingerprint);
        Assert.True(first.Certificate.HasPrivateKey);
    }

    [Fact]
    public void ADamagedIdentityIsReplacedNotTrusted()
    {
        using (var made = ServerIdentity.LoadOrCreate(_folder)) { }
        File.WriteAllText(Path.Combine(_folder, "server-key.pem"), "not a key");
        using var repaired = ServerIdentity.LoadOrCreate(_folder);
        Assert.True(repaired.Certificate.HasPrivateKey);
    }

    [Fact]
    public void FingerprintsAreComparedWhateverTheirGroupingAndCase()
    {
        const string raw = "A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F90";
        var grouped = ServerIdentity.Format(raw);
        Assert.StartsWith("A1B2-C3D4-E5F6-0718-", grouped);
        Assert.Equal(16, grouped.Split('-').Length);
        Assert.True(ServerIdentity.Same(raw, grouped));
        Assert.True(ServerIdentity.Same(raw.ToLowerInvariant(), grouped));
        Assert.False(ServerIdentity.Same(raw, raw[..^1] + "1"));
        Assert.False(ServerIdentity.Same(raw, null));
        Assert.False(ServerIdentity.Same("", ""));      // nothing is the same as nothing
    }

    [Fact]
    public async Task AnEncryptedConnectionWorksForAClientThatKnowsTheFingerprintAndForPlainHttpWhereAllowed()
    {
        using var identity = ServerIdentity.LoadOrCreate(_folder);
        await using var server = new LocalHttpServer(new HttpServerOptions(IPAddress.Loopback, 0, Certificate: identity.Certificate, AllowPlain: true), Hello());
        server.Start();
        using var http = Pinned(identity.Fingerprint);
        var secure = await http.GetStringAsync($"https://127.0.0.1:{server.Port}/x");
        Assert.Contains("\"secure\":true", secure);
        using var plain = new HttpClient();
        Assert.Contains("\"secure\":false", await plain.GetStringAsync($"http://127.0.0.1:{server.Port}/x"));
    }

    private static HttpClient Pinned(string fingerprint) => new(new SocketsHttpHandler
    {
        SslOptions = { RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null && ServerIdentity.Same(ServerIdentity.FingerprintOf(certificate), fingerprint) }
    });

    [Fact]
    public async Task AClientRefusesAServerWhoseFingerprintIsNotTheOneItTrusted()
    {
        using var identity = ServerIdentity.LoadOrCreate(_folder);
        await using var server = new LocalHttpServer(new HttpServerOptions(IPAddress.Loopback, 0, Certificate: identity.Certificate), Hello());
        server.Start();
        using var stranger = Pinned(new string('A', 64));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => stranger.GetStringAsync($"https://127.0.0.1:{server.Port}/x"));
        using var nobody = Pinned("");
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => nobody.GetStringAsync($"https://127.0.0.1:{server.Port}/x"));
    }

    [Fact]
    public async Task ANetworkServerAnswersAPlainRequestWithAReasonInsteadOfServingIt()
    {
        using var identity = ServerIdentity.LoadOrCreate(_folder);
        var reached = false;
        await using var server = new LocalHttpServer(new HttpServerOptions(IPAddress.Loopback, 0, Certificate: identity.Certificate, AllowPlain: false), (_, _) => { reached = true; return Task.FromResult(HttpResponse.Empty(200)); });
        server.Start();
        using var plain = new HttpClient();
        using var response = await plain.GetAsync($"http://127.0.0.1:{server.Port}/v1/health");
        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
        Assert.Contains("encryption_required", await response.Content.ReadAsStringAsync());
        Assert.False(reached);
        using var garbage = new TcpClient();
        await garbage.ConnectAsync(IPAddress.Loopback, server.Port);
        await garbage.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HELLO\r\n"));
        using var memory = new MemoryStream();
        await garbage.GetStream().CopyToAsync(memory);
        Assert.StartsWith("HTTP/1.1 426", Encoding.ASCII.GetString(memory.ToArray()));
    }

    [Fact]
    public async Task ADeadHandshakeDoesNotHoldTheServer()
    {
        using var identity = ServerIdentity.LoadOrCreate(_folder);
        await using var server = new LocalHttpServer(new HttpServerOptions(IPAddress.Loopback, 0, HeaderTimeout: TimeSpan.FromMilliseconds(300), Certificate: identity.Certificate), Hello());
        server.Start();
        using var silent = new TcpClient();
        await silent.ConnectAsync(IPAddress.Loopback, server.Port);
        await silent.GetStream().WriteAsync(new byte[] { 0x16, 0x03, 0x01 });      // starts a handshake and never finishes
        using var pinned = Pinned(identity.Fingerprint);
        Assert.Contains("hello", await pinned.GetStringAsync($"https://127.0.0.1:{server.Port}/x"));
        using var memory = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await silent.GetStream().CopyToAsync(memory, timeout.Token);                // the server closed it
    }

    [Fact]
    public async Task TheProbeShowsTheFingerprintAndSendsNoCredentials()
    {
        using var identity = ServerIdentity.LoadOrCreate(_folder);
        string? seen = null;
        await using var server = new LocalHttpServer(new HttpServerOptions(IPAddress.Loopback, 0, Certificate: identity.Certificate), (request, _) =>
        {
            seen = request.Header("Authorization");
            return Task.FromResult(HttpResponse.Json(200, new RemoteHealth("ok", "Kitchen", "Server", "1.2.3", true, request.IsSecure)));
        });
        server.Start();
        var probe = await RemoteServerClient.ProbeAsync(new Uri($"https://127.0.0.1:{server.Port}"), default);
        Assert.Equal(identity.Fingerprint, probe.Fingerprint);
        Assert.Equal("Kitchen", probe.Health.Name);
        Assert.True(probe.Health.PasswordRequired);
        Assert.True(probe.Health.Encrypted);
        Assert.Null(seen);
    }

    [Fact]
    public async Task ARemoteClientTalksToTheServerOnlyWithTheRightFingerprintAndSendsThePassword()
    {
        using var identity = ServerIdentity.LoadOrCreate(_folder);
        string? seen = null;
        await using var server = new LocalHttpServer(new HttpServerOptions(IPAddress.Loopback, 0, Certificate: identity.Certificate), (request, _) =>
        {
            seen = request.Header("Authorization");
            return Task.FromResult(request.Path == "/v1/server"
                ? HttpResponse.Json(200, new RemoteServerInfo("Kitchen", "Server", "1.2.3", true, true, true, [], false, 0))
                : HttpResponse.Error(404, "not_found", "no"));
        });
        server.Start();
        var address = new Uri($"https://127.0.0.1:{server.Port}");
        using (var right = new RemoteServerClient(address, "k7m2-pq9x-w4hd", identity.Fingerprint))
        {
            var info = await right.InfoAsync(default);
            Assert.Equal("Kitchen", info.Name);
            Assert.Equal("Bearer k7m2-pq9x-w4hd", seen);
        }
        seen = null;
        using (var wrong = new RemoteServerClient(address, "k7m2-pq9x-w4hd", new string('B', 64)))
        {
            var error = await Assert.ThrowsAsync<RemoteException>(() => wrong.InfoAsync(default));
            Assert.True(error.IsIdentityChanged);
            Assert.Null(seen);                 // the password never reached a server that was not the one trusted
        }
        using (var unpinned = new RemoteServerClient(address, "secret", null))
        {
            Assert.True((await Assert.ThrowsAsync<RemoteException>(() => unpinned.InfoAsync(default))).IsIdentityChanged);
            Assert.Null(seen);
        }
    }

    [Fact]
    public async Task AnAddressNobodyAnswersIsReportedAsUnreachable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var error = await Assert.ThrowsAsync<RemoteException>(() => RemoteServerClient.ProbeAsync(new Uri($"http://127.0.0.1:{port}"), default));
        Assert.True(error.IsUnreachable);
        await using var other = new LocalHttpServer(new HttpServerOptions(IPAddress.Loopback, 0), (_, _) => Task.FromResult(HttpResponse.Text(200, "a web server, not ours")));
        other.Start();
        Assert.Equal("not_a_server", (await Assert.ThrowsAsync<RemoteException>(() => RemoteServerClient.ProbeAsync(new Uri($"http://127.0.0.1:{other.Port}"), default))).Code);
    }

    // ---- discovery ------------------------------------------------------------------------------------------------------------------

    private static ServerAnnouncement Announcement(string id = "abc", string name = "Kitchen", int port = 8642) =>
        new(id, name, port, true, true, "Server", "1.2.3", new string('A', 64));

    [Fact]
    public void ABeaconSurvivesTheJourneyAndNothingElseIsBelieved()
    {
        var decoded = ServerBeacon.Decode(ServerBeacon.Encode(Announcement()));
        Assert.Equal(Announcement(), decoded);
        Assert.Null(ServerBeacon.Decode("not json"u8));
        Assert.Null(ServerBeacon.Decode("{\"magic\":\"someone-else\",\"v\":1,\"server\":{}}"u8));
        Assert.Null(ServerBeacon.Decode(new byte[5000]));
        Assert.Null(ServerBeacon.Decode([]));
        var badPort = Encoding.UTF8.GetBytes("{\"magic\":\"mockingbird-server\",\"v\":1,\"server\":{\"id\":\"x\",\"name\":\"n\",\"port\":70000,\"tls\":true,\"passwordRequired\":false,\"edition\":\"Server\",\"version\":\"1\",\"fingerprint\":\"AA\"}}");
        Assert.Null(ServerBeacon.Decode(badPort));
    }

    [Fact]
    public void ANameIsShortenedAndStrippedOfControlCharacters()
    {
        var wild = Announcement(name: "Kit\u0007chen\r\n" + new string('x', 200));
        var decoded = ServerBeacon.Decode(ServerBeacon.Encode(wild))!;
        Assert.Equal(60, decoded.Name.Length);
        Assert.DoesNotContain(decoded.Name, char.IsControl);
        Assert.StartsWith("Kitchen", decoded.Name);
    }

    [Fact]
    public async Task AServerHeardOnTheNetworkIsListedAndForgottenWhenItGoesQuiet()
    {
        var port = FreeUdpPort();
        var clock = new FakeClock();
        var announcement = Announcement();
        await using var listener = new BeaconListener(port, TimeSpan.FromSeconds(8), () => clock.Now);
        Assert.True(listener.Start());
        var changes = 0;
        listener.Changed += () => Interlocked.Increment(ref changes);
        await using (var sender = new BeaconSender(() => announcement, port, TimeSpan.FromMilliseconds(50), () => [new IPEndPoint(IPAddress.Loopback, port)]))
        {
            sender.Start();
            await WaitAsync(() => listener.Servers.Count == 1);
            var heard = listener.Servers.Single();
            Assert.Equal("Kitchen", heard.Announcement.Name);
            Assert.True(heard.ThisComputer);                                          // it came from this computer
            Assert.Equal($"https://127.0.0.1:8642/", heard.Uri.ToString());
            announcement = announcement with { Name = "Pantry" };
            await WaitAsync(() => listener.Servers.Single().Announcement.Name == "Pantry");   // a rename shows
            Assert.True(changes >= 2);
        }
        clock.Now += TimeSpan.FromSeconds(30);
        Assert.Empty(listener.Servers);                                              // quiet for longer than the lifetime
    }

    [Fact]
    public async Task AFloodOfMadeUpServersFillsNoMoreThanAHundredEntries()
    {
        var port = FreeUdpPort();
        await using var listener = new BeaconListener(port);
        Assert.True(listener.Start());
        using var socket = new UdpClient();
        for (var i = 0; i < 300; i++) await socket.SendAsync(ServerBeacon.Encode(Announcement(id: "id" + i, name: "Fake " + i)), new IPEndPoint(IPAddress.Loopback, port));
        await WaitAsync(() => listener.Servers.Count >= 100);
        await Task.Delay(300);
        Assert.Equal(100, listener.Servers.Count);
    }

    [Fact]
    public async Task ASecondListenerCanShareThePortAndAPortThatIsTakenJustMeansNothingIsFound()
    {
        var port = FreeUdpPort();
        await using var first = new BeaconListener(port);
        await using var second = new BeaconListener(port);
        Assert.True(first.Start());
        Assert.True(second.Start());                                                   // both windows on one computer
        using var blocker = new UdpClient { ExclusiveAddressUse = true };
        blocker.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var busy = ((IPEndPoint)blocker.Client.LocalEndPoint!).Port;
        await using var exclusive = new BeaconListener(busy);
        Assert.False(exclusive.Start());                                               // an exclusive owner: nothing to discover, no crash
    }

    private sealed class FakeClock { public DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero); }

    private static int FreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline) { if (condition()) return; await Task.Delay(25); }
        throw new TimeoutException("The condition was not reached.");
    }
}
