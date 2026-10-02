using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TriAsr.Infrastructure;

namespace TriAsr.Persistence.Tests;

public sealed class UpdateServiceTests
{
    private const string ReleaseUrl = "https://github.com/PufferfishGaming/Mockingbird/releases/download/download/Mockingbird-Studio-Setup.exe";
    private static readonly byte[] Installer = [1, 2, 3, 4, 5, 6, 7, 8];
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string Manifest(string version = "0.1.17", string? url = ReleaseUrl, string? sha256 = null, long? bytes = null, int schema = 1, string notes = "Fixes and improvements")
        => JsonSerializer.Serialize(new { schema, version, url, sha256 = sha256 ?? Hash(Installer), bytes = bytes ?? Installer.Length, notes });

    private static UpdateService Service(Func<HttpRequestMessage, HttpResponseMessage> respond, UpdateOptions? options = null)
        => new(options ?? UpdateOptions.GitHub, new HttpClient(new Handler(respond)));

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData("0.1.15", true, "0.1.15.0")]
    [InlineData("v0.1.16", true, "0.1.16.0")]
    [InlineData("0.1.16-rc1+abc123", true, "0.1.16.0")]
    [InlineData("1.2.3.4", true, "1.2.3.4")]
    [InlineData("nonsense", false, "0.0.0.0")]
    [InlineData("", false, "0.0.0.0")]
    public void VersionsParseIntoComparableFourPartNumbers(string text, bool valid, string expected)
    {
        Assert.Equal(valid, UpdateService.TryParseVersion(text, out var version));
        Assert.Equal(expected, version.ToString());
    }

    [Fact]
    public void VersionsCompareNumericallyNotAsText()
    {
        UpdateService.TryParseVersion("0.1.9", out var nine);
        UpdateService.TryParseVersion("0.1.10", out var ten);
        UpdateService.TryParseVersion("0.1.16", out var three);
        UpdateService.TryParseVersion("0.1.16.0", out var four);
        Assert.True(ten > nine);
        Assert.Equal(three, four);
    }

    [Fact]
    public async Task NewerReleaseIsOfferedWithItsVerifiedDetails()
    {
        using var service = Service(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.StartsWith("MockingbirdStudio/0.1.16", request.Headers.UserAgent.ToString());
            return Json(Manifest());
        });
        var offer = await service.CheckAsync("0.1.16");
        Assert.NotNull(offer);
        Assert.Equal("0.1.17", offer.VersionText);
        Assert.Equal(Hash(Installer), offer.Sha256);
        Assert.Equal(Installer.Length, offer.Bytes);
        Assert.Equal("Fixes and improvements", offer.Notes);
    }

    [Fact]
    public async Task ManifestSavedWithAByteOrderMarkIsStillRead()
    {
        var body = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(Manifest())).ToArray();
        using var service = Service(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        Assert.Equal("0.1.17", (await service.CheckAsync("0.1.16"))!.VersionText);
    }

    [Theory]
    [InlineData("0.1.16")]
    [InlineData("0.1.15")]
    [InlineData("0.0.1")]
    public async Task SameOrOlderReleaseIsNeverOffered(string published)
    {
        using var service = Service(_ => Json(Manifest(version: published)));
        Assert.Null(await service.CheckAsync("0.1.16"));
    }

    [Theory]
    [InlineData("http://github.com/PufferfishGaming/Mockingbird/releases/download/download/Setup.exe")]
    [InlineData("https://evil.example/PufferfishGaming/Mockingbird/releases/download/download/Setup.exe")]
    [InlineData("https://github.com/someone-else/repo/releases/download/download/Setup.exe")]
    [InlineData("https://github.com.evil.example/PufferfishGaming/Mockingbird/releases/download/download/Setup.exe")]
    [InlineData("https://user:pass@github.com/PufferfishGaming/Mockingbird/releases/download/download/Setup.exe")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("not a url")]
    public async Task DownloadAddressMustBeTheProjectsOwnHttpsReleaseLocation(string url)
    {
        using var service = Service(_ => Json(Manifest(url: url)));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync("0.1.16"));
    }

    [Fact]
    public async Task MalformedManifestsAreRejectedWithoutOffers()
    {
        foreach (var body in new[]
        {
            Manifest(sha256: "abc"), Manifest(sha256: new string('z', 64)), Manifest(bytes: 0), Manifest(bytes: UpdateService.MaxInstallerBytes + 1),
            Manifest(schema: 2), Manifest(version: "banana"), "{not json", "null", "[]"
        })
        {
            using var service = Service(_ => Json(body));
            await Assert.ThrowsAnyAsync<InvalidDataException>(() => service.CheckAsync("0.1.16"));
        }
    }

    [Fact]
    public async Task OversizedManifestIsRefusedBeforeItIsParsed()
    {
        using var service = Service(_ => Json(new string(' ', UpdateService.MaxManifestBytes + 10)));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync("0.1.16"));
    }

    [Fact]
    public async Task ServerErrorsSurfaceAsFailuresNotOffers()
    {
        using var service = Service(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        await Assert.ThrowsAsync<HttpRequestException>(() => service.CheckAsync("0.1.16"));
    }

    [Fact]
    public async Task OverlongNotesAreTrimmed()
    {
        using var service = Service(_ => Json(Manifest(notes: new string('x', 5000))));
        var offer = await service.CheckAsync("0.1.16");
        Assert.True(offer!.Notes.Length <= 1501);
    }

    [Fact]
    public void LoopbackOverrideIsOnlyHonoredForLocalHttp()
    {
        var previous = Environment.GetEnvironmentVariable(UpdateOptions.ManifestEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(UpdateOptions.ManifestEnvironmentVariable, "http://127.0.0.1:5999/latest.json");
            var local = UpdateOptions.FromEnvironment();
            Assert.True(local.IsTestSource); Assert.True(local.AllowLoopbackHttp);
            foreach (var hostile in new[] { "http://evil.example/latest.json", "https://127.0.0.1/latest.json", "ftp://127.0.0.1/x", "garbage" })
            {
                Environment.SetEnvironmentVariable(UpdateOptions.ManifestEnvironmentVariable, hostile);
                Assert.Equal(UpdateOptions.GitHub, UpdateOptions.FromEnvironment());
            }
            Environment.SetEnvironmentVariable(UpdateOptions.ManifestEnvironmentVariable, null);
            Assert.Equal(UpdateOptions.GitHub, UpdateOptions.FromEnvironment());
        }
        finally { Environment.SetEnvironmentVariable(UpdateOptions.ManifestEnvironmentVariable, previous); }
    }

    [Fact]
    public async Task VerifiedDownloadIsPublishedWithProgress()
    {
        var root = TempDirectory();
        try
        {
            using var service = Service(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("latest.json", StringComparison.Ordinal)) return Json(Manifest());
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Installer) };
            });
            var offer = (await service.CheckAsync("0.1.16"))!;
            var reports = new List<DownloadProgress>();
            var path = await service.DownloadAsync(offer, root, new SyncProgress(reports.Add));
            Assert.Equal(Installer, await File.ReadAllBytesAsync(path));
            Assert.Equal("Mockingbird-Studio-Setup-0.1.17.exe", Path.GetFileName(path));
            Assert.Empty(Directory.GetFiles(root, "*.partial"));
            Assert.Equal(Installer.Length, reports[^1].Received);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task EachEditionDownloadsAnInstallerNamedAfterItselfFromItsOwnManifest()
    {
        foreach (var edition in new[] { "Server", "Client" })
        {
            var root = TempDirectory();
            try
            {
                var asked = new List<string>();
                using var service = Service(request =>
                {
                    asked.Add(request.RequestUri!.AbsolutePath);
                    if (request.RequestUri.AbsolutePath.EndsWith(".json", StringComparison.Ordinal)) return Json(Manifest());
                    return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Installer) };
                }, UpdateOptions.ForEdition(edition));
                var offer = (await service.CheckAsync("0.1.16"))!;
                Assert.EndsWith($"/latest-{edition.ToLowerInvariant()}.json", asked[0]);
                var path = await service.DownloadAsync(offer, root);
                Assert.Equal($"Mockingbird-{edition}-Setup-0.1.17.exe", Path.GetFileName(path));
                // Leftovers of any edition's installer are cleared, nothing else.
                File.WriteAllText(Path.Combine(root, "Mockingbird-Studio-Setup-0.1.16.exe"), "x");
                File.WriteAllText(Path.Combine(root, "keep.txt"), "x");
                UpdateService.RemoveStaleDownloads(root);
                Assert.Equal(["keep.txt"], Directory.GetFiles(root).Select(Path.GetFileName));
            }
            finally { Directory.Delete(root, true); }
        }
    }

    [Fact]
    public async Task TamperedDownloadIsDiscardedAndNeverHandedOver()
    {
        var root = TempDirectory();
        try
        {
            using var service = Service(request => request.RequestUri!.AbsolutePath.EndsWith("latest.json", StringComparison.Ordinal)
                ? Json(Manifest())
                : new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4, 5, 6, 7, 9]) });
            var offer = (await service.CheckAsync("0.1.16"))!;
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(offer, root));
            Assert.Contains("SHA256", error.Message);
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(12)]
    public async Task DownloadWithTheWrongSizeIsRefused(int served)
    {
        var root = TempDirectory();
        try
        {
            using var service = Service(request => request.RequestUri!.AbsolutePath.EndsWith("latest.json", StringComparison.Ordinal)
                ? Json(Manifest())
                : new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[served]) });
            var offer = (await service.CheckAsync("0.1.16"))!;
            await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(offer, root));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CancelledDownloadLeavesNothingBehind()
    {
        var root = TempDirectory();
        try
        {
            using var service = Service(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Installer) });
            var offer = service.Validate(new UpdateManifest(1, "0.1.17", ReleaseUrl, Hash(Installer), Installer.Length));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAsync(offer, root, null, cancellation.Token));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void StaleInstallersAreClearedButOtherFilesAreKept()
    {
        var root = TempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "Mockingbird-Studio-Setup-0.1.17.0.exe"), "x");
            File.WriteAllText(Path.Combine(root, "Mockingbird-Studio-Setup-0.1.17.0.exe.partial"), "x");
            File.WriteAllText(Path.Combine(root, "keep.txt"), "x");
            UpdateService.RemoveStaleDownloads(root);
            Assert.Equal(["keep.txt"], Directory.GetFiles(root).Select(Path.GetFileName));
            UpdateService.RemoveStaleDownloads(Path.Combine(root, "missing"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void HelperScriptWaitsForTheAppRunsTheInstallerAndRestartsTheApp()
    {
        var script = UpdateLauncher.BuildScript(@"C:\Temp\Mockingbird-Studio-Setup-0.1.17.exe", @"C:\Users\x\AppData\Local\Programs\TriASR\TriAsr.App.exe", 4242, @"C:\Data\Config\update-result.json", "0.1.17.0");
        var lines = script.Split('\n');
        Assert.Contains("Wait-Process -Id 4242", lines[1]);
        Assert.Contains("'/passive','/norestart'", script);
        Assert.True(Array.FindIndex(lines, line => line.Contains("/passive")) < Array.FindIndex(lines, line => line.StartsWith("Start-Process -FilePath 'C:\\Users")));
        Assert.Contains("update-result.json", script);
        Assert.EndsWith("TriAsr.App.exe'", script);
    }

    [Fact]
    public void HelperScriptQuotesPathsSoTheyCannotInjectCommands()
    {
        var hostile = @"C:\Temp\x'; Remove-Item C:\ -Recurse; '.exe";
        var script = UpdateLauncher.BuildScript(hostile, @"C:\App\it's.exe", 1, @"C:\r.json", "1.0", "--flag 'a'");
        Assert.Contains(@"'C:\Temp\x''; Remove-Item C:\ -Recurse; ''.exe'", script);
        Assert.Contains("'C:\\App\\it''s.exe'", script);
        Assert.Contains("-ArgumentList '--flag ''a'''", script);
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
}
