using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using TriAsr.App;

namespace TriAsr.Ui.SmokeTests;

/// <summary>The API as the window runs it: off by default, started from the saved settings, restricted to this computer unless the network is allowed.</summary>
public sealed class ApiSettingsTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline) { if (await condition()) return; await Task.Delay(50); }
        throw new TimeoutException("Not reached: " + because);
    }

    private static HttpClient Client(int port, string? key)
    {
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(20) };
        if (key is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private static async Task SettledAsync(ShellViewModel shell, SettingsStore store, Func<TriAsr.App.AppSettings, bool> saved)
    {
        await WaitForAsync(async () => saved(await store.LoadAsync()), "the settings are saved");
        await WaitForAsync(() => Task.FromResult(shell.Status == Loc.T("Preferences saved locally")), "the background save finishes before the folder is removed");
    }

    [Fact]
    public async Task TheApiIsOffByDefaultAndHasNoKeyUntilItIsTurnedOn()
    {
        var root = NewRoot(); var before = Loc.Instance.Language;
        try
        {
            using var host = App.App.CreateHost(root);
            var shell = host.Services.GetRequiredService<ShellViewModel>();
            await shell.InitializeAsync();
            Assert.False(shell.ApiEnabled);
            Assert.False(shell.ApiAllowNetwork);
            Assert.Equal("", shell.ApiKey);
            Assert.False(shell.HasApiKey);          // nothing to show or copy until the API is turned on
            Assert.Equal(8642, shell.ApiPort);
            Assert.Equal("The API is off.", shell.ApiStatus);
            Assert.False((await host.Services.GetRequiredService<SettingsStore>().LoadAsync()).ApiEnabled);
        }
        finally { Loc.Instance.SetLanguage(before); TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task ASavedApiComesBackListeningOnThisComputerAndAnswersOnlyToTheKey()
    {
        var root = NewRoot(); var port = FreePort(); var before = Loc.Instance.Language;
        try
        {
            using var host = App.App.CreateHost(root);
            await host.Services.GetRequiredService<SettingsStore>().SaveAsync(new(ApiEnabled: true, ApiPort: port, ApiKey: "mbk-saved-key-0123456789"));
            var shell = host.Services.GetRequiredService<ShellViewModel>();
            await shell.InitializeAsync();
            await WaitForAsync(() => Task.FromResult(shell.ApiStatus.StartsWith("Listening on this computer only")), "the server starts");
            Assert.Contains($"http://127.0.0.1:{port}", shell.ApiStatus);
            Assert.Equal("mbk-saved-key-0123456789", shell.ApiKey);
            using (var anonymous = Client(port, null))
            {
                Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/v1/health")).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/languages")).StatusCode);
            }
            using (var keyed = Client(port, shell.ApiKey)) Assert.Equal(HttpStatusCode.OK, (await keyed.GetAsync("/v1/languages")).StatusCode);

            shell.StopApiForExit();
            await WaitForAsync(async () => { try { using var probe = new TcpClient(); await probe.ConnectAsync(IPAddress.Loopback, port); return false; } catch (SocketException) { return true; } }, "the port is released");
        }
        finally { Loc.Instance.SetLanguage(before); TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task TurningItOnMakesAKeyAndSavesItAndANewKeyReplacesTheOldOne()
    {
        var root = NewRoot(); var port = FreePort(); var before = Loc.Instance.Language;
        try
        {
            using var host = App.App.CreateHost(root);
            var shell = host.Services.GetRequiredService<ShellViewModel>();
            var store = host.Services.GetRequiredService<SettingsStore>();
            await shell.InitializeAsync();
            shell.ApiPortText = port.ToString();
            shell.ApiEnabled = true;
            Assert.StartsWith("mbk-", shell.ApiKey);
            Assert.Equal(44, shell.ApiKey.Length);
            Assert.True(shell.HasApiKey);
            await WaitForAsync(() => Task.FromResult(shell.ApiStatus.StartsWith("Listening")), "the server starts");
            var first = shell.ApiKey;
            using (var client = Client(port, first)) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/languages")).StatusCode);

            shell.NewApiKeyCommand.Execute(null);
            Assert.NotEqual(first, shell.ApiKey);
            using (var old = Client(port, first)) Assert.Equal(HttpStatusCode.Unauthorized, (await old.GetAsync("/v1/languages")).StatusCode);
            using (var renewed = Client(port, shell.ApiKey)) Assert.Equal(HttpStatusCode.OK, (await renewed.GetAsync("/v1/languages")).StatusCode);

            var key = shell.ApiKey;
            await SettledAsync(shell, store, saved => saved.ApiEnabled && saved.ApiPort == port && saved.ApiKey == key);
            shell.ApiEnabled = false;
            await WaitForAsync(() => Task.FromResult(shell.ApiStatus == "The API is off."), "the status says it is off");
            await WaitForAsync(async () => { try { using var probe = new TcpClient(); await probe.ConnectAsync(IPAddress.Loopback, port); return false; } catch (SocketException) { return true; } }, "switching it off releases the port");
            await SettledAsync(shell, store, saved => !saved.ApiEnabled);
            Assert.Equal(key, (await store.LoadAsync()).ApiKey);   // the key is kept for the next time
            shell.StopApiForExit();
        }
        finally { Loc.Instance.SetLanguage(before); TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task AWrongPortIsExplainedAndAPortThatIsTakenIsReported()
    {
        var root = NewRoot(); var port = FreePort(); var before = Loc.Instance.Language;
        try
        {
            using var host = App.App.CreateHost(root);
            var shell = host.Services.GetRequiredService<ShellViewModel>();
            var store = host.Services.GetRequiredService<SettingsStore>();
            await shell.InitializeAsync();
            shell.ApiPortText = "80";
            shell.ApiEnabled = true;
            await WaitForAsync(() => Task.FromResult(shell.ApiStatus == "The port must be a number from 1024 to 65535."), "the port is refused");

            using var taken = new TcpListener(IPAddress.Loopback, port);
            taken.Start();
            shell.ApiPortText = port.ToString();
            await WaitForAsync(() => Task.FromResult(shell.ApiStatus.StartsWith("The API could not start")), "the busy port is reported");
            Assert.True(shell.HasError);
            taken.Stop();

            shell.ApiPortText = FreePort().ToString();
            await WaitForAsync(() => Task.FromResult(shell.ApiStatus.StartsWith("Listening")), "a free port works");
            var chosen = shell.ApiPort;
            await SettledAsync(shell, store, saved => saved.ApiPort == chosen);
            shell.StopApiForExit();
        }
        finally { Loc.Instance.SetLanguage(before); TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task TheStatusIsSaidInTheLanguageAndAgainWhenItChanges()
    {
        var root = NewRoot(); var port = FreePort(); var before = Loc.Instance.Language;
        try
        {
            using var host = App.App.CreateHost(root);
            await host.Services.GetRequiredService<SettingsStore>().SaveAsync(new(ApiEnabled: true, ApiPort: port, ApiKey: "mbk-saved-key-0123456789"));
            var shell = host.Services.GetRequiredService<ShellViewModel>();
            var store = host.Services.GetRequiredService<SettingsStore>();
            await shell.InitializeAsync();
            await WaitForAsync(() => Task.FromResult(shell.ApiStatus.StartsWith("Listening")), "the server starts");
            shell.Language = "de";
            Assert.StartsWith("Hört nur auf diesem Computer:", shell.ApiStatus);
            shell.Language = "fr";
            Assert.StartsWith("À l'écoute sur cet ordinateur uniquement :", shell.ApiStatus);
            shell.Language = "en";
            Assert.StartsWith("Listening on this computer only", shell.ApiStatus);
            await SettledAsync(shell, store, saved => saved.Language == "en");
            shell.StopApiForExit();
        }
        finally { Loc.Instance.SetLanguage(before); TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task APortOutsideTheRangeOrAMissingKeyInTheFileIsSetRightWhenSettingsAreRead()
    {
        var root = NewRoot();
        try
        {
            using var host = App.App.CreateHost(root);
            var store = host.Services.GetRequiredService<SettingsStore>();
            await store.SaveAsync(new(ApiPort: 22, ApiKey: null!));
            var loaded = await store.LoadAsync();
            Assert.Equal(8642, loaded.ApiPort);
            Assert.Equal("", loaded.ApiKey);
            Assert.False(loaded.ApiEnabled);
            Assert.False(loaded.ApiAllowNetwork);
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public void TheExampleCommandsNameTheAddressAndHideTheKeyUntilItIsShown()
    {
        var root = NewRoot();
        try
        {
            using var host = App.App.CreateHost(root);
            var shell = host.Services.GetRequiredService<ShellViewModel>();
            shell.ApiKey = "mbk-secret-0123456789";
            Assert.DoesNotContain("mbk-secret", shell.ApiExample);
            Assert.DoesNotContain("mbk-secret", shell.ApiKeyShown);
            Assert.Contains("YOUR_KEY", shell.ApiExample);
            shell.ShowApiKey = true;
            Assert.Contains("mbk-secret-0123456789", shell.ApiExample);
            Assert.Equal("mbk-secret-0123456789", shell.ApiKeyShown);
            Assert.Contains("http://127.0.0.1:8642/v1/transcriptions", shell.ApiExample);
            Assert.Contains("/v1/audio/transcriptions", shell.ApiExample);
        }
        finally { TestCleanup.Delete(root); }
    }
}
