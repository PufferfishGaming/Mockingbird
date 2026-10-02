using System.Net;
using System.Security.Cryptography;
using System.Text;
using TriAsr.Application;
using TriAsr.Infrastructure;

namespace TriAsr.Persistence.Tests;

/// <summary>Fetching the sound of a link (ADR-0018): which addresses are allowed, the helper program's installation, direct downloads and the helper's output.</summary>
public sealed class LinkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

    public LinkTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var item in _disposables) item.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private LocalHttpServer Serve(HttpHandler handler)
    {
        var server = new LocalHttpServer(new HttpServerOptions(IPAddress.Loopback, 0), handler);
        server.Start();
        _disposables.Add(new ServerStopper(server));
        return server;
    }

    private sealed class ServerStopper(LocalHttpServer server) : IDisposable
    {
        public void Dispose() => server.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static string Address(LocalHttpServer server, string path) => $"http://127.0.0.1:{server.Port}{path}";

    private static readonly LinkFetchOptions OwnComputer = new(AllowPrivateNetwork: true);

    // ---- which addresses are links ------------------------------------------------------------------------------------------------

    [Fact]
    public void WhatAPersonPastesBecomesAWebAddressOrIsRefusedWithAReason()
    {
        Assert.Equal("https://example.com/talk.mp3", LinkPolicy.Parse("  example.com/talk.mp3 ").AbsoluteUri);
        Assert.Equal("http://example.com/a", LinkPolicy.Parse("http://example.com/a").AbsoluteUri);
        Assert.Equal("https://example.com/watch?v=1&t=5", LinkPolicy.Parse("https://example.com/watch?v=1&t=5").AbsoluteUri);

        Assert.Equal(LinkMessages.Paste, Assert.Throws<LinkException>(() => LinkPolicy.Parse("   ")).Message);
        Assert.Equal(LinkMessages.Paste, Assert.Throws<LinkException>(() => LinkPolicy.Parse(null)).Message);
        foreach (var notAnAddress in new[] { "two words", "https://exa mple.com", "https://example.com/a\nb", "https://", "://nothing", new string('a', LinkPolicy.MaxLength + 1) })
            Assert.Equal(LinkMessages.NotAddress, Assert.Throws<LinkException>(() => LinkPolicy.Parse(notAnAddress)).Message);
        foreach (var other in new[] { "ftp://example.com/a.mp3", "file:///C:/secret.wav", "javascript:alert(1)", "data:audio/wav;base64,AAAA", "\\\\server\\share\\a.wav" })
            Assert.Throws<LinkException>(() => LinkPolicy.Parse(other));
        Assert.Equal(LinkMessages.OnlyWeb, Assert.Throws<LinkException>(() => LinkPolicy.Parse("ftp://example.com/a.mp3")).Message);
        Assert.Equal(LinkMessages.HasUserInfo, Assert.Throws<LinkException>(() => LinkPolicy.Parse("https://user:secret@example.com/a.mp3")).Message);
    }

    [Theory]
    [InlineData("8.8.8.8", true)] [InlineData("93.184.216.34", true)] [InlineData("172.32.0.1", true)] [InlineData("100.63.255.255", true)] [InlineData("2001:4860:4860::8888", true)]
    [InlineData("127.0.0.1", false)] [InlineData("10.1.2.3", false)] [InlineData("172.16.0.1", false)] [InlineData("172.31.255.255", false)] [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)] [InlineData("100.64.0.1", false)] [InlineData("100.127.255.255", false)] [InlineData("0.0.0.0", false)] [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)] [InlineData("198.18.0.1", false)] [InlineData("192.0.2.1", false)]
    [InlineData("::1", false)] [InlineData("::", false)] [InlineData("fe80::1", false)] [InlineData("fc00::1", false)] [InlineData("fd12:3456::1", false)] [InlineData("ff02::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:127.0.0.1", false)] [InlineData("::ffff:10.0.0.1", false)] [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("::127.0.0.1", false)] [InlineData("64:ff9b::7f00:1", false)] [InlineData("64:ff9b::808:808", true)]
    public void OnlyAddressesOfThePublicInternetArePublic(string address, bool expected) => Assert.Equal(expected, LinkPolicy.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task AServerDoesNotFollowLinksIntoThePrivateNetworkButTheOwnersComputerMay()
    {
        foreach (var inside in new[] { "http://127.0.0.1/a.mp3", "http://localhost/a.mp3", "http://printer.localhost/a.mp3", "http://10.0.0.5:8080/a.mp3", "http://[::1]/a.mp3", "http://192.168.0.1/", "http://169.254.169.254/latest/meta-data/" })
            Assert.Equal(LinkMessages.PrivateNetwork, (await Assert.ThrowsAsync<LinkException>(() => LinkPolicy.EnsureAllowedAsync(new Uri(inside), false, default))).Message);
        foreach (var inside in new[] { "http://127.0.0.1/a.mp3", "http://localhost/a.mp3", "http://10.0.0.5/" })
            await LinkPolicy.EnsureAllowedAsync(new Uri(inside), true, default);
        await LinkPolicy.EnsureAllowedAsync(new Uri("http://8.8.8.8/a.mp3"), false, default);     // an address needs no lookup
    }

    // ---- the helper program -------------------------------------------------------------------------------------------------------

    private static string Sums(byte[] exe, string other = "") =>
        $"{Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes("linux build")))}  yt-dlp\n{Convert.ToHexString(SHA256.HashData(exe)).ToLowerInvariant()}  yt-dlp.exe\n{other}"
        + $"{Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes("x86 build")))}  yt-dlp_x86.exe\n";

    private sealed class Release
    {
        public byte[] Exe = Encoding.ASCII.GetBytes("MZ pretend program, first version");
        public string? SumsText;
        public int ExeRequests, SumsRequests;
        public bool Missing;
    }

    private (LocalHttpServer Server, Release Release) ServeRelease()
    {
        var release = new Release();
        var server = Serve((request, _) =>
        {
            if (release.Missing) return Task.FromResult(HttpResponse.Error(404, "not_found", "gone"));
            if (request.Path == "/release/yt-dlp.exe") { release.ExeRequests++; return Task.FromResult(HttpResponse.Bytes(200, release.Exe, "application/octet-stream")); }
            if (request.Path == "/release/SHA2-256SUMS") { release.SumsRequests++; return Task.FromResult(HttpResponse.Text(200, release.SumsText ?? Sums(release.Exe))); }
            return Task.FromResult(HttpResponse.Error(404, "not_found", "no"));
        });
        return (server, release);
    }

    private YtDlpTool Tool(LocalHttpServer server)
    {
        var tool = new YtDlpTool(Path.Combine(_root, "Tool"), $"http://127.0.0.1:{server.Port}/release/");
        _disposables.Add(tool);
        return tool;
    }

    [Fact]
    public void TheChecksumOfTheWindowsProgramIsTheOnlyLineThatCounts()
    {
        var exe = Encoding.ASCII.GetBytes("a");
        var wanted = Convert.ToHexString(SHA256.HashData(exe));
        Assert.Equal(wanted, YtDlpTool.ParseHash(Sums(exe)));
        Assert.Equal(wanted, YtDlpTool.ParseHash($"{wanted.ToLowerInvariant()} *yt-dlp.exe\r\n"));
        Assert.Null(YtDlpTool.ParseHash($"{wanted}  yt-dlp_x86.exe\n{wanted}  yt-dlp_win.zip\n"));
        Assert.Null(YtDlpTool.ParseHash("abc123  yt-dlp.exe\n"));                              // not a SHA-256
        Assert.Null(YtDlpTool.ParseHash(new string('g', 64) + "  yt-dlp.exe\n"));              // not hexadecimal
        Assert.Null(YtDlpTool.ParseHash(""));
    }

    [Fact]
    public async Task TheHelperIsDownloadedOnceCheckedAgainstTheChecksumAndKeptUntilANewerOneIsPublished()
    {
        var (server, release) = ServeRelease();
        var tool = Tool(server);
        Assert.False(tool.Installed);
        Assert.False(await tool.UpdateAvailableAsync(default));                                // nothing to update

        var percents = new List<double>();
        Assert.True(await tool.InstallAsync(new Progress<double>(percents.Add), default));
        Assert.True(tool.Installed);
        Assert.Equal(release.Exe, await File.ReadAllBytesAsync(tool.ExecutablePath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(tool.ExecutablePath)!, "*.partial"));

        Assert.False(await tool.InstallAsync(null, default));                                   // up to date: nothing is downloaded again
        Assert.Equal(1, release.ExeRequests);
        Assert.False(await tool.UpdateAvailableAsync(default));

        release.Exe = Encoding.ASCII.GetBytes("MZ pretend program, second version, a bit longer");
        Assert.True(await tool.UpdateAvailableAsync(default));
        Assert.True(await tool.InstallAsync(null, default));
        Assert.Equal(release.Exe, await File.ReadAllBytesAsync(tool.ExecutablePath));
        Assert.True(tool.Installed);
        Assert.False(await tool.UpdateAvailableAsync(default));
        Assert.Equal(2, release.ExeRequests);
    }

    [Fact]
    public async Task AFileThatDoesNotMatchThePublishedChecksumIsNeverInstalled()
    {
        var (server, release) = ServeRelease();
        release.SumsText = Sums(Encoding.ASCII.GetBytes("something else entirely"));
        var tool = Tool(server);
        Assert.Equal(LinkMessages.HelperChecksum, (await Assert.ThrowsAsync<LinkException>(() => tool.InstallAsync(null, default))).Message);
        Assert.False(tool.Installed);
        Assert.False(File.Exists(tool.ExecutablePath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(tool.ExecutablePath)!));          // not even a partial file is left

        // a good copy that is already installed survives a bad update
        release.SumsText = null;
        Assert.True(await tool.InstallAsync(null, default));
        var good = await File.ReadAllBytesAsync(tool.ExecutablePath);
        release.Exe = Encoding.ASCII.GetBytes("tampered");
        release.SumsText = Sums(Encoding.ASCII.GetBytes("the real one"));
        await Assert.ThrowsAsync<LinkException>(() => tool.InstallAsync(null, default));
        Assert.Equal(good, await File.ReadAllBytesAsync(tool.ExecutablePath));
        Assert.True(tool.Installed);
    }

    [Fact]
    public async Task AReleaseThatCannotBeReadIsReportedAndInstallsNothing()
    {
        var (server, release) = ServeRelease();
        var tool = Tool(server);
        release.Missing = true;
        Assert.Equal(LinkMessages.HelperDownload, (await Assert.ThrowsAsync<LinkException>(() => tool.InstallAsync(null, default))).Message);
        release.Missing = false;
        release.SumsText = "no useful line in this list\n";
        Assert.Equal(LinkMessages.HelperDownload, (await Assert.ThrowsAsync<LinkException>(() => tool.InstallAsync(null, default))).Message);
        Assert.False(tool.Installed);
        using var unreachable = new YtDlpTool(Path.Combine(_root, "Other"), "http://127.0.0.1:1/release/");
        Assert.Equal(LinkMessages.HelperDownload, (await Assert.ThrowsAsync<LinkException>(() => unreachable.InstallAsync(null, default))).Message);
    }

    [Fact]
    public async Task AnInstalledFileThatWasChangedAfterwardsIsNotTrusted()
    {
        var (server, _) = ServeRelease();
        var tool = Tool(server);
        await tool.InstallAsync(null, default);
        Assert.True(tool.Installed);
        await File.AppendAllTextAsync(tool.ExecutablePath, "extra");
        Assert.False(tool.Installed);
        Assert.True(await tool.InstallAsync(null, default));                                    // and installing puts the right file back
        Assert.True(tool.Installed);
    }

    // ---- links straight to a file --------------------------------------------------------------------------------------------------

    private static readonly byte[] Sound = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();

    private DirectLinkDownloader Direct(Func<Uri, bool, CancellationToken, Task>? gate = null)
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        _disposables.Add(client);
        return new DirectLinkDownloader(client, gate);
    }

    private string Folder(string name = "fetch") => Path.Combine(_root, name);

    [Fact]
    public async Task ALinkToAnAudioFileIsDownloadedWithItsNameAndReportsHowFarItHasCome()
    {
        var server = Serve((_, _) => Task.FromResult(HttpResponse.Bytes(200, Sound, "audio/mpeg")));
        var percents = new System.Collections.Concurrent.ConcurrentQueue<double>();     // Progress calls back on other threads
        var file = await Direct().TryAsync(new Uri(Address(server, "/episodes/Talk%20One.mp3?token=abc")), Folder(), OwnComputer, new Progress<double>(percents.Enqueue), default);
        Assert.NotNull(file);
        Assert.Equal(Path.Combine(Folder(), "Talk One.mp3"), file.Path);
        Assert.Equal("Talk One", file.Title);
        Assert.Equal(Sound, await File.ReadAllBytesAsync(file.Path));
        await Task.Delay(100);
        Assert.Equal(100, percents.Max());
        Assert.Empty(Directory.GetFiles(Folder(), "*.partial"));

        var second = await Direct().TryAsync(new Uri(Address(server, "/episodes/Talk%20One.mp3")), Folder(), OwnComputer, null, default);
        Assert.Equal(Path.Combine(Folder(), "Talk One (2).mp3"), second!.Path);                  // a name that is taken is not overwritten
    }

    [Fact]
    public async Task TheSitesNameForTheFileIsUsedButNeverAPathOrACharacterWindowsRefuses()
    {
        var server = Serve((request, _) => Task.FromResult(HttpResponse.Bytes(200, Sound, "audio/mpeg").With("Content-Disposition",
            request.Path == "/a" ? "attachment; filename=\"..\\..\\evil:name?.mp3\"" : request.Path == "/b" ? "attachment; filename*=UTF-8''Sz%C3%A9p%20nap.m4a" : "attachment; filename=\"CON.wav\"")));
        var a = await Direct().TryAsync(new Uri(Address(server, "/a")), Folder(), OwnComputer, null, default);
        Assert.Equal(Folder(), Path.GetDirectoryName(a!.Path));
        Assert.DoesNotContain(':', Path.GetFileName(a.Path));
        Assert.DoesNotContain('?', Path.GetFileName(a.Path));
        Assert.EndsWith(".mp3", a.Path);
        var b = await Direct().TryAsync(new Uri(Address(server, "/b")), Folder(), OwnComputer, null, default);
        Assert.Equal("Szép nap.m4a", Path.GetFileName(b!.Path));
        var c = await Direct().TryAsync(new Uri(Address(server, "/c")), Folder(), OwnComputer, null, default);
        Assert.Equal("link.wav", Path.GetFileName(c!.Path));                                    // a reserved device name is not a file name
    }

    [Fact]
    public async Task APageThatIsNotAMediaFileIsLeftToTheHelperAndNothingIsSaved()
    {
        var server = Serve((request, _) => Task.FromResult(request.Path switch
        {
            "/page" => HttpResponse.Text(200, "<html>a video page</html>", "text/html; charset=utf-8"),
            "/missing" => HttpResponse.Error(404, "not_found", "no"),
            "/blob" => HttpResponse.Bytes(200, Sound, "application/octet-stream"),
            "/clip.mp4" => HttpResponse.Bytes(200, Sound, "application/octet-stream"),
            "/Download.M4A" => HttpResponse.Bytes(200, Sound, "binary/octet-stream"),
            _ => HttpResponse.Bytes(200, Sound, "audio/wav")
        }));
        var direct = Direct();
        Assert.Null(await direct.TryAsync(new Uri(Address(server, "/page")), Folder(), OwnComputer, null, default));
        Assert.Null(await direct.TryAsync(new Uri(Address(server, "/missing")), Folder(), OwnComputer, null, default));
        Assert.Null(await direct.TryAsync(new Uri(Address(server, "/blob")), Folder(), OwnComputer, null, default));        // an unnamed blob is not known to be sound
        Assert.False(Directory.Exists(Folder()) && Directory.GetFileSystemEntries(Folder()).Length > 0);
        Assert.NotNull(await direct.TryAsync(new Uri(Address(server, "/clip.mp4")), Folder(), OwnComputer, null, default));  // but a name that says so is enough
        Assert.NotNull(await direct.TryAsync(new Uri(Address(server, "/Download.M4A")), Folder(), OwnComputer, null, default));
    }

    [Fact]
    public async Task RedirectsAreFollowedAndEachStepGoesThroughTheSameCheckAsTheFirstAddress()
    {
        var server = Serve((request, _) => Task.FromResult(request.Path switch
        {
            "/short" => HttpResponse.Empty(302).With("Location", "/episode.mp3"),
            "/hop1" => HttpResponse.Empty(301).With("Location", "/hop2"),
            "/hop2" => HttpResponse.Empty(307).With("Location", "/secret/inner.mp3"),
            "/loop" => HttpResponse.Empty(302).With("Location", "/loop"),
            "/episode.mp3" or "/secret/inner.mp3" => HttpResponse.Bytes(200, Sound, "audio/mpeg"),
            _ => HttpResponse.Error(404, "not_found", "no")
        }));
        var seen = new List<(string Path, bool AllowPrivate)>();
        var direct = Direct((uri, allowPrivate, _) =>
        {
            seen.Add((uri.AbsolutePath, allowPrivate));
            return uri.AbsolutePath.StartsWith("/secret", StringComparison.Ordinal) && !allowPrivate ? throw new LinkException(LinkMessages.PrivateNetwork) : Task.CompletedTask;
        });

        var followed = await direct.TryAsync(new Uri(Address(server, "/short")), Folder(), new LinkFetchOptions(AllowPrivateNetwork: false), null, default);
        Assert.Equal("episode.mp3", Path.GetFileName(followed!.Path));
        Assert.Equal([("/short", false), ("/episode.mp3", false)], seen);

        seen.Clear();
        var refused = await Assert.ThrowsAsync<LinkException>(() => direct.TryAsync(new Uri(Address(server, "/hop1")), Folder("two"), new LinkFetchOptions(AllowPrivateNetwork: false), null, default));
        Assert.Equal(LinkMessages.PrivateNetwork, refused.Message);                              // the last step led somewhere it must not
        Assert.Equal(["/hop1", "/hop2", "/secret/inner.mp3"], seen.Select(step => step.Path));
        Assert.False(Directory.Exists(Folder("two")) && Directory.GetFileSystemEntries(Folder("two")).Length > 0);

        Assert.NotNull(await direct.TryAsync(new Uri(Address(server, "/hop1")), Folder("three"), OwnComputer, null, default));    // on the owner's own computer it is fine
        Assert.Equal(LinkMessages.TooManyRedirects, (await Assert.ThrowsAsync<LinkException>(() => direct.TryAsync(new Uri(Address(server, "/loop")), Folder("four"), OwnComputer, null, default))).Message);
    }

    [Fact]
    public async Task ARealPublicOnlyDownloaderRefusesALoopbackAddressBeforeAnyRequestIsMade()
    {
        var requests = 0;
        var server = Serve((_, _) => { requests++; return Task.FromResult(HttpResponse.Bytes(200, Sound, "audio/mpeg")); });
        using var direct = new DirectLinkDownloader();
        var refused = await Assert.ThrowsAsync<LinkException>(() => direct.TryAsync(new Uri(Address(server, "/a.mp3")), Folder(), new LinkFetchOptions(), null, default));
        Assert.Equal(LinkMessages.PrivateNetwork, refused.Message);
        Assert.Equal(0, requests);
        Assert.NotNull(await direct.TryAsync(new Uri(Address(server, "/a.mp3")), Folder(), OwnComputer, null, default));       // the owner's own computer may
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task ARecordingLargerThanTheLimitIsRefusedWhetherOrNotTheSiteSaysHowLargeItIs()
    {
        var big = new byte[10_000];
        var server = Serve((_, _) => Task.FromResult(HttpResponse.Bytes(200, big, "audio/mpeg")));
        var options = new LinkFetchOptions(AllowPrivateNetwork: true, MaxBytes: 4096);
        Assert.Equal(LinkMessages.TooLarge, (await Assert.ThrowsAsync<LinkException>(() => Direct().TryAsync(new Uri(Address(server, "/big.mp3")), Folder(), options, null, default))).Message);
        Assert.False(Directory.Exists(Folder()) && Directory.GetFileSystemEntries(Folder()).Length > 0);    // nothing was written, not even a partial file
        Assert.NotNull(await Direct().TryAsync(new Uri(Address(server, "/big.mp3")), Folder(), options with { MaxBytes = 20_000 }, null, default));
        Assert.Equal(4L * 1024 * 1024 * 1024, new LinkFetchOptions().Limit);
    }

    [Fact]
    public async Task AnEmptyFileIsNotARecording()
    {
        var server = Serve((_, _) => Task.FromResult(HttpResponse.Bytes(200, [], "audio/mpeg")));
        Assert.Equal(LinkMessages.NoSound, (await Assert.ThrowsAsync<LinkException>(() => Direct().TryAsync(new Uri(Address(server, "/empty.mp3")), Folder(), OwnComputer, null, default))).Message);
        Assert.False(Directory.Exists(Folder()) && Directory.GetFileSystemEntries(Folder()).Length > 0);
    }

    [Fact]
    public async Task AnAddressNobodyAnswersIsReportedAsUnreachable()
    {
        var server = new LocalHttpServer(new HttpServerOptions(IPAddress.Loopback, 0), (_, _) => Task.FromResult(HttpResponse.Empty(200)));
        server.Start();
        var port = server.Port;
        await server.DisposeAsync();
        Assert.Equal(LinkMessages.Unreachable, (await Assert.ThrowsAsync<LinkException>(() => Direct().TryAsync(new Uri($"http://127.0.0.1:{port}/a.mp3"), Folder(), OwnComputer, null, default))).Message);
    }

    // ---- pages, through the helper -----------------------------------------------------------------------------------------------

    private sealed class FakeRunner(Func<ProcessRequest, int> run) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ProcessResult(run(request), "", "", 0.1));
        }
    }

    private async Task<YtDlpTool> InstalledToolAsync()
    {
        var (server, _) = ServeRelease();
        var tool = Tool(server);
        await tool.InstallAsync(null, default);
        return tool;
    }

    [Fact]
    public void TheHelperIsGivenOneAddressAfterTheOptionsEndSoThatAnAddressCanNeverActAsAnOption()
    {
        var link = new Uri("https://video.example/watch?v=1&list=2&x=%22quoted%22");
        var arguments = YtDlpPageFetcher.Arguments(link, Folder(), new LinkFetchOptions(MaxBytes: 12345));
        Assert.Equal(["--", link.AbsoluteUri], arguments.TakeLast(2));
        Assert.Equal(1, arguments.Count(argument => argument == "--"));
        foreach (var required in new[] { "--ignore-config", "--no-playlist", "--no-simulate", "--windows-filenames" }) Assert.Contains(required, arguments);
        Assert.Equal("12345", arguments[arguments.ToList().IndexOf("--max-filesize") + 1]);
        Assert.Equal("bestaudio/best", arguments[arguments.ToList().IndexOf("--format") + 1]);
        Assert.StartsWith(Folder() + Path.DirectorySeparatorChar, arguments[arguments.ToList().IndexOf("--output") + 1]);       // the file lands in the folder that was named
        Assert.DoesNotContain("--exec", arguments);
        Assert.DoesNotContain("--config-locations", arguments);
    }

    [Fact]
    public async Task WhatTheHelperPrintsBecomesTheFileTheTitleAndTheProgress()
    {
        var tool = await InstalledToolAsync();
        var runner = new FakeRunner(request =>
        {
            Directory.CreateDirectory(request.WorkingDirectory);
            var path = Path.Combine(request.WorkingDirectory, "Great Talk [abc123].webm");
            File.WriteAllBytes(path, Sound);
            foreach (var line in new[] { "MBPROGRESS 0 1000 NA\r", "MBPROGRESS 250 1000 NA", "MBPROGRESS 500 NA 2000", "MBPROGRESS 1000 1000 NA", "MBPROGRESS NA NA NA", "some other line", $"MBFILE {path}", "MBTITLE Great Talk: part one" })
                request.ErrorLine!(line);
            return 0;
        });
        var percents = new System.Collections.Concurrent.ConcurrentQueue<double>();
        var fetched = await new YtDlpPageFetcher(tool, runner).FetchAsync(new Uri("https://video.example/watch?v=abc123"), Folder(), OwnComputer, new Progress<double>(percents.Enqueue), default);
        Assert.Equal(Path.Combine(Folder(), "Great Talk [abc123].webm"), fetched.Path);
        Assert.Equal("Great Talk: part one", fetched.Title);
        Assert.Equal(tool.ExecutablePath, runner.Requests.Single().Executable);
        await Task.Delay(100);
        Assert.Equal([0d, 25d, 100d], percents.Distinct().Order());        // 0, 25 twice, 100, a line without a number says nothing, and the end
    }

    [Fact]
    public async Task APathThatIsNotInTheFolderIsNotTrustedAndTheNewestFileIsUsedInstead()
    {
        var tool = await InstalledToolAsync();
        var outside = Path.Combine(_root, "elsewhere.wav");
        var runner = new FakeRunner(request =>
        {
            File.WriteAllBytes(outside, Sound);
            File.WriteAllBytes(Path.Combine(request.WorkingDirectory, "older.webm"), Sound);
            File.SetLastWriteTimeUtc(Path.Combine(request.WorkingDirectory, "older.webm"), DateTime.UtcNow.AddMinutes(-5));
            File.WriteAllBytes(Path.Combine(request.WorkingDirectory, "newer.m4a"), Sound);
            File.WriteAllBytes(Path.Combine(request.WorkingDirectory, "half.webm.part"), Sound);
            request.ErrorLine!($"MBFILE {outside}");
            return 0;
        });
        var fetched = await new YtDlpPageFetcher(tool, runner).FetchAsync(new Uri("https://video.example/a"), Folder(), OwnComputer, null, default);
        Assert.Equal(Path.Combine(Folder(), "newer.m4a"), fetched.Path);
        Assert.Equal("newer", fetched.Title);
    }

    [Theory]
    [InlineData("ERROR: [generic] Unsupported URL: https://video.example/x", LinkMessages.NoSound)]
    [InlineData("ERROR: [youtube] abc: Sign in to confirm you are not a bot", LinkMessages.NeedsLogin)]
    [InlineData("ERROR: [vimeo] 1: This video is private", LinkMessages.NeedsLogin)]
    [InlineData("ERROR: File is larger than max-filesize (1000 bytes > 500 bytes). Aborting.", LinkMessages.TooLarge)]
    [InlineData("ERROR: Unable to download webpage: <urlopen error [Errno 11001] getaddrinfo failed>", LinkMessages.Unreachable)]
    [InlineData("", LinkMessages.Failed)]
    public void WhatTheHelperComplainsAboutIsSaidInOurOwnWords(string complaint, string expected) => Assert.Equal(expected, YtDlpPageFetcher.Explain(complaint));

    [Fact]
    public void AComplaintThatIsNotRecognisedIsPassedOnInTheSitesWordsButShortened()
    {
        var shown = YtDlpPageFetcher.Explain("ERROR: Something odd happened with the codec");
        Assert.Equal(LinkMessages.FailedPrefix + "Something odd happened with the codec", shown);
        Assert.True(YtDlpPageFetcher.Explain("ERROR: " + new string('x', 1000)).Length < 400);
    }

    [Fact]
    public async Task AFailureOfTheHelperIsAnExceptionWithTheReasonAndLeavesNothingBehind()
    {
        var tool = await InstalledToolAsync();
        var runner = new FakeRunner(request => { request.ErrorLine!("ERROR: [generic] Unsupported URL: https://video.example/x"); return 1; });
        var error = await Assert.ThrowsAsync<LinkException>(() => new YtDlpPageFetcher(tool, runner).FetchAsync(new Uri("https://video.example/x"), Folder(), OwnComputer, null, default));
        Assert.Equal(LinkMessages.NoSound, error.Message);
        var silent = new FakeRunner(_ => 0);                                                    // success but no file at all
        Assert.Equal(LinkMessages.NoSound, (await Assert.ThrowsAsync<LinkException>(() => new YtDlpPageFetcher(tool, silent).FetchAsync(new Uri("https://video.example/x"), Folder("empty"), OwnComputer, null, default))).Message);
    }

    [Theory]
    [InlineData("1000 1000 NA", 100.0)] [InlineData("250 1000 NA", 25.0)] [InlineData("500 NA 2000", 25.0)] [InlineData("1500 1000 NA", 100.0)]
    [InlineData("NA NA NA", null)] [InlineData("100 NA NA", null)] [InlineData("100", null)] [InlineData("", null)] [InlineData("12 0 NA", null)]
    public void ProgressIsReadFromWhateverTotalTheHelperKnows(string text, double? expected)
    {
        var read = YtDlpPageFetcher.ParseProgress(text);
        if (expected is null) Assert.Null(read); else Assert.Equal(expected.Value, read!.Value, 6);
    }

    // ---- all together -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFileLinkNeedsNoHelperAPageDoesAndAPrivateAddressIsRefusedBeforeAnythingIsRequested()
    {
        var requests = new List<string>();
        var server = Serve((request, _) =>
        {
            lock (requests) requests.Add(request.Path);
            return Task.FromResult(request.Path == "/page" ? HttpResponse.Text(200, "<html></html>", "text/html") : HttpResponse.Bytes(200, Sound, "audio/mpeg"));
        });
        var (releaseServer, _) = ServeRelease();
        var tool = Tool(releaseServer);
        var runner = new FakeRunner(request =>
        {
            var path = Path.Combine(request.WorkingDirectory, "from page.webm");
            File.WriteAllBytes(path, Sound);
            request.ErrorLine!("MBFILE " + path);
            return 0;
        });
        var direct = Direct();
        var fetcher = new LinkFetcher(direct, new YtDlpPageFetcher(tool, runner), tool);

        Assert.False(fetcher.PagesReady);
        Assert.NotNull(await fetcher.FetchAsync(new Uri(Address(server, "/talk.mp3")), Folder("file"), OwnComputer, null, default));
        Assert.Empty(runner.Requests);

        Assert.Equal(LinkMessages.NeedsHelper, (await Assert.ThrowsAsync<LinkException>(() => fetcher.FetchAsync(new Uri(Address(server, "/page")), Folder("page"), OwnComputer, null, default))).Message);

        await tool.InstallAsync(null, default);
        Assert.True(fetcher.PagesReady);
        var page = await fetcher.FetchAsync(new Uri(Address(server, "/page")), Folder("page"), OwnComputer, null, default);
        Assert.Equal("from page.webm", Path.GetFileName(page.Path));
        Assert.Single(runner.Requests);

        requests.Clear();
        Assert.Equal(LinkMessages.PrivateNetwork, (await Assert.ThrowsAsync<LinkException>(() => fetcher.FetchAsync(new Uri(Address(server, "/talk.mp3")), Folder("private"), new LinkFetchOptions(), null, default))).Message);
        Assert.Empty(requests);
        Assert.Single(runner.Requests);                                                         // and the helper was not started for it either
    }

    /// <summary>A real download from the internet, run only when asked for: <c>TRIASR_TEST_NETWORK=1</c>. It fetches a short public-domain recording from archive.org.</summary>
    [Fact]
    public async Task ARealPublicFileCanBeDownloadedWhenTheNetworkTestsAreSwitchedOn()
    {
        if (Environment.GetEnvironmentVariable("TRIASR_TEST_NETWORK") != "1") return;
        using var direct = new DirectLinkDownloader();
        var fetched = await direct.TryAsync(new Uri("https://archive.org/download/testmp3testfile/mpthreetest.mp3"), Folder("net"), new LinkFetchOptions(), null, default);
        Assert.NotNull(fetched);
        Assert.True(new FileInfo(fetched.Path).Length > 10_000);
    }
    /// <summary>
    /// The real helper against a site that is not a video site's own: it is downloaded from its project (and checked), then given the page of an item on archive.org
    /// and has to deliver the sound of it. Run only when asked for: <c>TRIASR_TEST_NETWORK=1</c>.
    /// </summary>
    [Fact]
    public async Task TheRealHelperFetchesTheSoundOfAPageOnAnotherSiteWhenTheNetworkTestsAreSwitchedOn()
    {
        if (Environment.GetEnvironmentVariable("TRIASR_TEST_NETWORK") != "1") return;
        using var tool = new YtDlpTool(Path.Combine(_root, "RealTool"));
        Assert.True(await tool.InstallAsync(null, default));
        Assert.True(tool.Installed);
        Assert.False(await tool.UpdateAvailableAsync(default));
        var percents = new System.Collections.Concurrent.ConcurrentQueue<double>();
        var fetcher = new LinkFetcher(new DirectLinkDownloader(), new YtDlpPageFetcher(tool, new ProcessRunner()), tool);
        var fetched = await fetcher.FetchAsync(new Uri("https://archive.org/details/testmp3testfile"), Folder("real-page"), new LinkFetchOptions(), new Progress<double>(percents.Enqueue), default);
        Assert.True(File.Exists(fetched.Path));
        Assert.True(new FileInfo(fetched.Path).Length > 10_000, fetched.Path);
        Assert.StartsWith(Folder("real-page"), fetched.Path);
        Assert.False(string.IsNullOrWhiteSpace(fetched.Title));
        Assert.Contains(100d, percents);

        var failed = await Assert.ThrowsAsync<LinkException>(() => fetcher.FetchAsync(new Uri("https://archive.org/details/this-item-does-not-exist-mockingbird-0000"), Folder("real-missing"), new LinkFetchOptions(), null, default));
        Assert.False(string.IsNullOrWhiteSpace(failed.Message));
    }
}