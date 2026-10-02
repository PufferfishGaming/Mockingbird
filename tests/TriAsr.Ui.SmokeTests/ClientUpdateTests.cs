using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TriAsr.App;
using TriAsr.Infrastructure;

namespace TriAsr.Ui.SmokeTests;

/// <summary>The Client edition checks its own release file and offers its own installer (ADR-0002, ADR-0014), with the same choices as Studio: later, skip this version, not at all.</summary>
public sealed class ClientUpdateTests
{
    private const string ReleaseUrl = "https://github.com/PufferfishGaming/Mockingbird/releases/download/download/Mockingbird-Client-Setup.exe";
    private static readonly byte[] Installer = [9, 8, 7, 6];

    private static string Manifest(string version) => JsonSerializer.Serialize(new
    {
        schema = 1, version, url = ReleaseUrl, sha256 = Convert.ToHexString(SHA256.HashData(Installer)), bytes = Installer.Length, notes = "Sends faster."
    });

    private sealed class Fixture : IDisposable
    {
        private readonly AppEdition _before = Edition.Current;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
        public List<Uri> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new(HttpStatusCode.NotFound);
        private Microsoft.Extensions.Hosting.IHost? _host;

        public Fixture() => Edition.Current = AppEdition.Client;

        private UpdateOptions _options = UpdateOptions.ForEdition("Client");
        public void UseOptions(UpdateOptions options) => _options = options;

        public ShellViewModel Shell()
        {
            _host?.Dispose();
            _host = App.App.CreateHost(Root, services => services.AddSingleton(new UpdateService(_options, new HttpClient(new Handler(request =>
            {
                Requests.Add(request.RequestUri!);
                return Respond(request);
            })))));
            return _host.Services.GetRequiredService<ShellViewModel>();
        }

        public ClientViewModel Client()
        {
            _host?.Dispose();
            _host = App.App.CreateHost(Root, services => services.AddSingleton(new UpdateService(_options, new HttpClient(new Handler(request =>
            {
                Requests.Add(request.RequestUri!);
                return Respond(request);
            })))));
            return _host.Services.GetRequiredService<ClientViewModel>();
        }

        public SettingsStore Store => _host!.Services.GetRequiredService<SettingsStore>();

        public void Dispose()
        {
            _host?.Dispose();
            Edition.Current = _before;
            TestCleanup.Delete(Root);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline) { if (await condition()) return; await Task.Delay(30); }
        throw new TimeoutException("Not reached.");
    }

    [Fact]
    public async Task TheServerEditionChecksForItsOwnUpdatesToo()
    {
        using var fixture = new Fixture();
        Edition.Current = AppEdition.Server;
        fixture.Respond = _ => Json(Manifest("99.0.0").Replace("Mockingbird-Client-Setup.exe", "Mockingbird-Server-Setup.exe"));
        fixture.UseOptions(UpdateOptions.ForEdition("Server"));
        var shell = fixture.Shell();
        await shell.InitializeAsync();
        await shell.RunUpdateCheckAsync(manual: false);
        Assert.EndsWith("/releases/download/download/latest-server.json", Assert.Single(fixture.Requests).AbsolutePath);
        Assert.True(shell.UpdateBannerVisible);
        Assert.Equal("Version 99.0.0 is available", shell.UpdateTitle);
        Assert.Contains("Mockingbird Server", shell.SetupTitle);     // the texts name the edition that is running
        await shell.Host.DisposeAsync();
    }

    [Fact]
    public async Task TheClientAsksForItsOwnReleaseFileAndShowsTheBanner()
    {
        using var fixture = new Fixture();
        fixture.Respond = _ => Json(Manifest("99.0.0"));
        var client = fixture.Client();
        await client.InitializeAsync();
        await client.RunUpdateCheckAsync(manual: false);
        var asked = Assert.Single(fixture.Requests);
        Assert.EndsWith("/releases/download/download/latest-client.json", asked.AbsolutePath);
        Assert.True(client.UpdateBannerVisible);
        Assert.Equal("Version 99.0.0 is available", client.UpdateTitle);
        Assert.Contains("Sends faster.", client.UpdateDetail);
        Assert.Contains("SHA256", client.UpdateDetail);
        Assert.Contains("Version 99.0.0 is available.", client.UpdateStatusText);
        await client.WhenSavedAsync();
        client.Close();
    }

    [Fact]
    public async Task SkippingAVersionIsRememberedAndAManualCheckShowsItAgain()
    {
        using var fixture = new Fixture();
        fixture.Respond = _ => Json(Manifest("99.0.0"));
        var client = fixture.Client();
        await client.InitializeAsync();
        await client.RunUpdateCheckAsync(manual: false);
        client.SkipUpdateCommand.Execute(null);
        Assert.False(client.UpdateBannerVisible);
        await WaitForAsync(async () => (await fixture.Store.LoadAsync()).SkippedUpdateVersion == "99.0.0");
        Assert.NotNull((await fixture.Store.LoadAsync()).LastUpdateCheckUtc);
        await client.WhenSavedAsync();
        client.Close();

        // A restart inside the 12-hour window does not contact the server.
        fixture.Requests.Clear();
        client = fixture.Client();
        await client.InitializeAsync();
        await client.RunUpdateCheckAsync(manual: false);
        Assert.Empty(fixture.Requests);
        Assert.False(client.UpdateBannerVisible);

        // After the window the skipped version stays hidden...
        var saved = await fixture.Store.LoadAsync();
        await fixture.Store.SaveAsync(saved with { LastUpdateCheckUtc = DateTimeOffset.UtcNow.AddHours(-13) });
        await client.WhenSavedAsync();
        client.Close();
        client = fixture.Client();
        await client.InitializeAsync();
        await client.RunUpdateCheckAsync(manual: false);
        Assert.Single(fixture.Requests);
        Assert.False(client.UpdateBannerVisible);
        Assert.Contains("skip", client.UpdateStatusText);

        // ...until the user asks.
        await client.RunUpdateCheckAsync(manual: true);
        Assert.True(client.UpdateBannerVisible);
        await client.WhenSavedAsync();
        client.Close();
    }

    [Fact]
    public async Task WithAutomaticChecksOffNothingIsRequestedUnlessTheUserAsks()
    {
        using var fixture = new Fixture();
        fixture.Respond = _ => Json(Manifest("99.0.0"));
        var client = fixture.Client();
        await client.InitializeAsync();
        client.AutoCheckUpdates = false;
        await WaitForAsync(async () => !(await fixture.Store.LoadAsync()).CheckForUpdates);
        await client.RunUpdateCheckAsync(manual: false);
        Assert.Empty(fixture.Requests);
        await client.WhenSavedAsync();
        client.Close();

        client = fixture.Client();
        await client.InitializeAsync();
        Assert.False(client.AutoCheckUpdates);
        await client.RunUpdateCheckAsync(manual: false);
        Assert.Empty(fixture.Requests);
        await client.RunUpdateCheckAsync(manual: true);
        Assert.Single(fixture.Requests);
        Assert.True(client.UpdateBannerVisible);
        await client.WhenSavedAsync();
        client.Close();
    }

    [Fact]
    public async Task ACurrentVersionAndAFailedCheckAreSaidPlainlyAndOnlyWhenAsked()
    {
        using var fixture = new Fixture();
        fixture.Respond = _ => Json(Manifest("0.0.1"));
        var client = fixture.Client();
        await client.InitializeAsync();
        await client.RunUpdateCheckAsync(manual: true);
        Assert.False(client.UpdateBannerVisible);
        Assert.StartsWith("You are running the latest version", client.UpdateStatusText);

        fixture.Respond = _ => throw new HttpRequestException("offline");
        await client.RunUpdateCheckAsync(manual: false);
        Assert.StartsWith("You are running the latest version", client.UpdateStatusText); // the quiet check said nothing
        await client.RunUpdateCheckAsync(manual: true);
        Assert.StartsWith("Could not check for updates:", client.UpdateStatusText);
        Assert.False(client.HasError);
        await client.WhenSavedAsync();
        client.Close();
    }

    [Fact]
    public async Task ACopyThatIsNotInstalledSaysItCannotUpdateItselfAndRefusesToTry()
    {
        using var fixture = new Fixture();
        fixture.Respond = _ => Json(Manifest("99.0.0"));
        var client = fixture.Client();
        await client.InitializeAsync();
        await client.RunUpdateCheckAsync(manual: true);
        Assert.False(client.CanSelfUpdate);
        Assert.Contains("cannot update itself", client.UpdateDetail);
        Assert.False(client.InstallUpdateCommand.CanExecute(null));
        await client.WhenSavedAsync();
        client.Close();
    }

    [Fact]
    public async Task TheUpdateTextsFollowTheInterfaceLanguage()
    {
        var before = Loc.Instance.Language;
        using var fixture = new Fixture();
        try
        {
            fixture.Respond = _ => Json(Manifest("99.0.0"));
            var client = fixture.Client();
            await client.InitializeAsync();
            await client.RunUpdateCheckAsync(manual: true);
            Assert.Equal("Version 99.0.0 is available", client.UpdateTitle);
            client.ChooseLanguage("hu");
            await WaitForAsync(() => Task.FromResult(client.UpdateTitle != "Version 99.0.0 is available"));
            Assert.Contains("99.0.0", client.UpdateTitle);
            Assert.DoesNotContain("available", client.UpdateStatusText);
            await client.WhenSavedAsync();
            client.Close();
        }
        finally { Loc.Instance.SetLanguage(before); }
    }
}
