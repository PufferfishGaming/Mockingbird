using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TriAsr.Infrastructure;

namespace TriAsr.Ui.SmokeTests;

public sealed class UpdateUiTests
{
    private const string ReleaseUrl = "https://github.com/PufferfishGaming/Mockingbird/releases/download/download/Mockingbird-Studio-Setup.exe";
    private static readonly byte[] Installer = [1, 2, 3, 4];

    private static string Manifest(string version) => JsonSerializer.Serialize(new
    {
        schema = 1, version, url = ReleaseUrl, sha256 = Convert.ToHexString(SHA256.HashData(Installer)), bytes = Installer.Length, notes = "Faster exports."
    });

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
        public int Requests;
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new(HttpStatusCode.NotFound);
        public Microsoft.Extensions.Hosting.IHost? Host;

        public App.ShellViewModel Shell(bool testSource = false)
        {
            Host?.Dispose();
            var options = testSource
                ? new UpdateOptions(UpdateOptions.GitHub.ManifestUri, "github.com", "/PufferfishGaming/Mockingbird/releases/download/", false, true)
                : UpdateOptions.GitHub;
            var host = App.App.CreateHost(Root, services => services.AddSingleton(new UpdateService(options, new HttpClient(new Handler(request =>
            {
                Interlocked.Increment(ref Requests);
                return Respond(request);
            })))));
            Host = host;
            return host.Services.GetRequiredService<App.ShellViewModel>();
        }
        public App.SettingsStore Store => Host!.Services.GetRequiredService<App.SettingsStore>();
        public void Dispose()
        {
            Host?.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    [Fact]
    public async Task NewerReleaseShowsTheBannerAndSkipSurvivesRestartUntilAManualCheck()
    {
        using var fixture = new Fixture();
        fixture.Respond = _ => Json(Manifest("99.0.0"));
        var shell = fixture.Shell();
        await shell.InitializeAsync();
        await shell.RunUpdateCheckAsync(manual: false);
        Assert.True(shell.UpdateBannerVisible);
        Assert.Equal("Version 99.0.0 is available", shell.UpdateTitle);
        Assert.Contains("Faster exports.", shell.UpdateDetail);
        Assert.Contains("SHA256", shell.UpdateDetail);
        Assert.Equal(1, fixture.Requests);

        await shell.SkipUpdateCommand.ExecuteAsync(null);
        Assert.False(shell.UpdateBannerVisible);
        var saved = await fixture.Store.LoadAsync();
        Assert.Equal("99.0.0", saved.SkippedUpdateVersion);
        Assert.NotNull(saved.LastUpdateCheckUtc);

        // A restart inside the 12-hour window does not even contact the server.
        fixture.Requests = 0;
        shell = fixture.Shell();
        await shell.InitializeAsync();
        await shell.RunUpdateCheckAsync(manual: false);
        Assert.Equal(0, fixture.Requests);
        Assert.False(shell.UpdateBannerVisible);

        // After the window passes, the skipped version stays hidden...
        await fixture.Store.SaveAsync(saved with { LastUpdateCheckUtc = DateTimeOffset.UtcNow.AddHours(-13) });
        shell = fixture.Shell();
        await shell.InitializeAsync();
        await shell.RunUpdateCheckAsync(manual: false);
        Assert.Equal(1, fixture.Requests);
        Assert.False(shell.UpdateBannerVisible);
        Assert.Contains("skip", shell.UpdateStatusText);

        // ...until the user asks explicitly.
        await shell.RunUpdateCheckAsync(manual: true);
        Assert.True(shell.UpdateBannerVisible);
    }

    [Fact]
    public async Task DisablingAutomaticChecksStopsAllNetworkTrafficButManualChecksStillWork()
    {
        using var fixture = new Fixture();
        fixture.Respond = _ => Json(Manifest("99.0.0"));
        var shell = fixture.Shell();
        await shell.InitializeAsync();
        shell.AutoCheckUpdates = false;
        await WaitForAsync(async () => !(await fixture.Store.LoadAsync()).CheckForUpdates);
        await shell.RunUpdateCheckAsync(manual: false);
        Assert.Equal(0, fixture.Requests);
        Assert.False(shell.UpdateBannerVisible);

        shell = fixture.Shell();
        await shell.InitializeAsync();
        Assert.False(shell.AutoCheckUpdates);
        await shell.RunUpdateCheckAsync(manual: false);
        Assert.Equal(0, fixture.Requests);
        await shell.RunUpdateCheckAsync(manual: true);
        Assert.Equal(1, fixture.Requests);
        Assert.True(shell.UpdateBannerVisible);
    }

    [Fact]
    public async Task OtherPreferenceChangesKeepTheUpdateChoices()
    {
        using var fixture = new Fixture();
        fixture.Respond = _ => Json(Manifest("99.0.0"));
        var shell = fixture.Shell();
        await shell.InitializeAsync();
        await shell.RunUpdateCheckAsync(manual: false);
        await shell.SkipUpdateCommand.ExecuteAsync(null);
        shell.SelectedTheme = "Dark";
        await WaitForAsync(async () => (await fixture.Store.LoadAsync()).Theme == "Dark");
        var saved = await fixture.Store.LoadAsync();
        Assert.Equal("99.0.0", saved.SkippedUpdateVersion);
        Assert.NotNull(saved.LastUpdateCheckUtc);
        Assert.True(saved.CheckForUpdates);
    }

    [Fact]
    public async Task UpToDateAndOlderReleasesShowNoBanner()
    {
        using var fixture = new Fixture();
        fixture.Respond = _ => Json(Manifest("0.0.1"));
        var shell = fixture.Shell();
        await shell.InitializeAsync();
        await shell.RunUpdateCheckAsync(manual: true);
        Assert.False(shell.UpdateBannerVisible);
        Assert.Null(shell.AvailableUpdate);
        Assert.Contains("latest version", shell.UpdateStatusText);
    }

    [Fact]
    public async Task FailedAutomaticCheckIsSilentWhileAManualCheckExplains()
    {
        using var fixture = new Fixture();
        fixture.Respond = _ => throw new HttpRequestException("offline");
        var shell = fixture.Shell();
        await shell.InitializeAsync();
        var before = shell.UpdateStatusText;
        await shell.RunUpdateCheckAsync(manual: false);
        Assert.False(shell.HasError);
        Assert.False(shell.UpdateBannerVisible);
        Assert.Equal(before, shell.UpdateStatusText);

        await shell.RunUpdateCheckAsync(manual: true);
        Assert.StartsWith("Could not check for updates", shell.UpdateStatusText);
        Assert.Contains("internet connection", shell.UpdateStatusText);
        Assert.False(shell.HasError);
    }

    [Fact]
    public async Task AnInvalidManifestNeverProducesAnOffer()
    {
        using var fixture = new Fixture();
        fixture.Respond = _ => Json(Manifest("99.0.0").Replace("github.com", "evil.example"));
        var shell = fixture.Shell();
        await shell.InitializeAsync();
        await shell.RunUpdateCheckAsync(manual: true);
        Assert.False(shell.UpdateBannerVisible);
        Assert.Null(shell.AvailableUpdate);
        Assert.StartsWith("Could not check for updates", shell.UpdateStatusText);
    }

    [Fact]
    public async Task UpdateIsRefusedWhileAnotherLongOperationRuns()
    {
        using var fixture = new Fixture();
        fixture.Respond = request => request.RequestUri!.AbsolutePath.EndsWith("latest.json", StringComparison.Ordinal)
            ? Json(Manifest("99.0.0")) : throw new InvalidOperationException("The installer must not be downloaded during a job.");
        var shell = fixture.Shell(testSource: true);
        await shell.InitializeAsync();
        await shell.RunUpdateCheckAsync(manual: true);
        Assert.True(shell.InstallUpdateCommand.CanExecute(null));
        shell.IsProcessing = true;
        var requestsBefore = fixture.Requests;
        await shell.InstallUpdateCommand.ExecuteAsync(null);
        Assert.Equal(requestsBefore, fixture.Requests);
        Assert.Contains("Finish the running", shell.UpdateDetail);
        Assert.False(shell.IsUpdateBusy);
        Assert.False(shell.HasError);
    }

    [Fact]
    public async Task OnlyTheInstalledCopyCanReplaceItself()
    {
        using var fixture = new Fixture();
        fixture.Respond = _ => Json(Manifest("99.0.0"));
        var shell = fixture.Shell();
        await shell.InitializeAsync();
        await shell.RunUpdateCheckAsync(manual: true);
        Assert.True(shell.UpdateBannerVisible);
        Assert.False(shell.CanSelfUpdate);
        Assert.False(shell.InstallUpdateCommand.CanExecute(null));
        Assert.Contains("cannot update itself", shell.UpdateDetail);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(3010, false)]
    [InlineData(1603, true)]
    public async Task TheOutcomeOfTheLastUpdateIsReportedOnceThenCleared(int exitCode, bool expectError)
    {
        using var fixture = new Fixture();
        var config = Path.Combine(fixture.Root, "Config");
        Directory.CreateDirectory(config);
        var result = Path.Combine(config, "update-result.json");
        await File.WriteAllTextAsync(result, JsonSerializer.Serialize(new { version = App.AppInfo.Version, exitCode }));
        var shell = fixture.Shell();
        await shell.InitializeAsync();
        Assert.Equal(expectError, shell.HasError);
        if (expectError) { Assert.Equal("The last update did not finish", shell.ErrorTitle); Assert.Contains("1603", shell.ErrorMessage); }
        else Assert.StartsWith("Updated to", shell.UpdateStatusText);
        Assert.False(File.Exists(result));
    }

    [Fact]
    public async Task UpdatePreferencesRoundTripThroughTheSettingsFile()
    {
        using var fixture = new Fixture();
        var shell = fixture.Shell();
        await shell.InitializeAsync();
        var settings = new App.AppSettings("Light", "Compact", AnimateErrors: false, CheckForUpdates: false,
            SkippedUpdateVersion: "1.2.3", LastUpdateCheckUtc: new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        await fixture.Store.SaveAsync(settings);
        Assert.Equal(settings, await fixture.Store.LoadAsync());
        // Settings written by earlier versions have none of these fields and must default to checking enabled.
        await File.WriteAllTextAsync(fixture.Store.FilePath, "{\"theme\":\"Dark\",\"density\":\"Compact\",\"version\":1,\"animateErrors\":true}");
        var legacy = await fixture.Store.LoadAsync();
        Assert.True(legacy.CheckForUpdates);
        Assert.Null(legacy.SkippedUpdateVersion);
        Assert.Null(legacy.LastUpdateCheckUtc);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline) { if (await condition()) return; await Task.Delay(50); }
        throw new TimeoutException("The expected state was not reached.");
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
}
