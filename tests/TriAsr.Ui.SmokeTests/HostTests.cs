using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using TriAsr.App;
using TriAsr.Infrastructure;

namespace TriAsr.Ui.SmokeTests;

/// <summary>Hosting as the window runs it: off by default, started from the saved settings, a name and an optional password, encrypted, announced on the network only when the network is allowed.</summary>
public sealed class HostTests
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

    private static HttpClient Plain(int port, string? password)
    {
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(20) };
        if (password is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", password);
        return client;
    }

    private static async Task SettledAsync(ShellViewModel shell, SettingsStore store, Func<TriAsr.App.AppSettings, bool> saved)
    {
        await WaitForAsync(async () => saved(await store.LoadAsync()), "the settings are saved");
        await WaitForAsync(() => Task.FromResult(shell.Status == Loc.T("Preferences saved locally")), "the background save finishes before the folder is removed");
    }

    private static async Task<bool> RefusesAsync(int port)
    {
        try { using var probe = new TcpClient(); await probe.ConnectAsync(IPAddress.Loopback, port); return false; }
        catch (SocketException) { return true; }
    }

    [Fact]
    public async Task OneButtonStartsAndStopsHostingAndTheChoiceOfWhoCanUseItIsOneSetting()
    {
        var root = NewRoot(); var port = FreePort(); var before = Loc.Instance.Language;
        try
        {
            using var host = App.App.CreateHost(root);
            var shell = host.Services.GetRequiredService<ShellViewModel>();
            await shell.InitializeAsync();
            shell.Host.PortText = port.ToString();
            Assert.False(shell.Host.Enabled);
            Assert.True(shell.Host.LocalOnly);
            Assert.True(await RefusesAsync(port));

            shell.Host.ToggleHostingCommand.Execute(null);
            Assert.True(shell.Host.Enabled);
            await WaitForAsync(() => Task.FromResult(shell.Host.Status.StartsWith("Listening on this computer only")), "the server starts");
            Assert.False(await RefusesAsync(port));

            // This computer only and the network are the two answers to one question.
            var changes = new List<string>();
            shell.Host.PropertyChanged += (_, change) => changes.Add(change.PropertyName!);
            Assert.Equal($"http://127.0.0.1:{port}", shell.Host.WebPageAddress);    // the page of the server, where a program on this computer reaches it
            shell.Host.AllowNetwork = true;
            Assert.Equal($"https://127.0.0.1:{port}", shell.Host.WebPageAddress);
            Assert.False(shell.Host.LocalOnly);
            Assert.Contains(nameof(HostViewModel.LocalOnly), changes);
            shell.Host.LocalOnly = true;
            Assert.False(shell.Host.AllowNetwork);
            shell.Host.LocalOnly = false;       // choosing the other half again changes nothing
            Assert.False(shell.Host.AllowNetwork);

            shell.Host.ToggleHostingCommand.Execute(null);
            Assert.False(shell.Host.Enabled);
            await WaitForAsync(() => Task.FromResult(shell.Host.Status == "The server is off."), "the server stops");
            Assert.True(await RefusesAsync(port));
            await SettledAsync(shell, host.Services.GetRequiredService<SettingsStore>(), saved => !saved.HostEnabled && !saved.HostAllowNetwork);
        }
        finally { Loc.Instance.SetLanguage(before); TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task HostingIsOffByDefaultAndNothingIsMadeUntilItIsUsed()
    {
        var root = NewRoot(); var before = Loc.Instance.Language;
        try
        {
            using var host = App.App.CreateHost(root);
            var shell = host.Services.GetRequiredService<ShellViewModel>();
            await shell.InitializeAsync();
            Assert.False(shell.Host.Enabled);
            Assert.False(shell.Host.AllowNetwork);
            Assert.False(shell.Host.HasPassword);
            Assert.Equal(8642, shell.Host.Port);
            Assert.Equal(Environment.MachineName, shell.Host.DisplayName);       // an empty name is the name of the computer
            Assert.Equal("The server is off.", shell.Host.Status);
            Assert.False(shell.Host.HasFingerprint);                              // no certificate is made for someone who never hosts
            Assert.False(File.Exists(Path.Combine(root, "Config", "Identity", "server-certificate.pem")));
        }
        finally { Loc.Instance.SetLanguage(before); TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task ASavedServerComesBackWithItsNameAndPasswordAndSpeaksBothPlainOnThisComputerAndEncrypted()
    {
        var root = NewRoot(); var port = FreePort(); var before = Loc.Instance.Language;
        try
        {
            using var host = App.App.CreateHost(root);
            await host.Services.GetRequiredService<SettingsStore>().SaveAsync(new(HostEnabled: true, HostName: "Kitchen", HostPort: port, HostPassword: "k7m2-pq9x-w4hd", HostId: "abc123"));
            var shell = host.Services.GetRequiredService<ShellViewModel>();
            await shell.InitializeAsync();
            await WaitForAsync(() => Task.FromResult(shell.Host.Status.StartsWith("Listening on this computer only")), "the server starts");
            Assert.Equal("Kitchen", shell.Host.DisplayName);
            Assert.Equal("abc123", shell.Host.ServerId);
            Assert.True(shell.Host.HasFingerprint);

            using (var anonymous = Plain(port, null))
            {
                var health = System.Text.Json.JsonDocument.Parse(await anonymous.GetStringAsync("/v1/health")).RootElement;
                Assert.Equal("Kitchen", health.GetProperty("name").GetString());
                Assert.True(health.GetProperty("passwordRequired").GetBoolean());
                Assert.False(health.GetProperty("encrypted").GetBoolean());
                Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/languages")).StatusCode);
            }
            using (var keyed = Plain(port, "k7m2-pq9x-w4hd")) Assert.Equal(HttpStatusCode.OK, (await keyed.GetAsync("/v1/languages")).StatusCode);

            // Encrypted, on the same port, to a client that knows the fingerprint it was shown.
            var probe = await RemoteServerClient.ProbeAsync(new Uri($"https://127.0.0.1:{port}"), default);
            Assert.True(probe.Health.Encrypted);
            Assert.True(ServerIdentity.Same(probe.Fingerprint, shell.Host.Fingerprint));
            using var secure = new RemoteServerClient(new Uri($"https://127.0.0.1:{port}"), "k7m2-pq9x-w4hd", probe.Fingerprint);
            Assert.Equal("Kitchen", (await secure.InfoAsync(default)).Name);
            using var wrongPassword = new RemoteServerClient(new Uri($"https://127.0.0.1:{port}"), "nope", probe.Fingerprint);
            Assert.True((await Assert.ThrowsAsync<RemoteException>(() => wrongPassword.InfoAsync(default))).IsAuthentication);

            shell.Host.StopForExit();
            await WaitForAsync(() => RefusesAsync(port), "the port is released");
        }
        finally { Loc.Instance.SetLanguage(before); TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task TurningItOnMakesTheIdentityAndAPasswordCanBeChangedOrRemovedWhileItRuns()
    {
        var root = NewRoot(); var port = FreePort(); var before = Loc.Instance.Language;
        try
        {
            using var host = App.App.CreateHost(root);
            var shell = host.Services.GetRequiredService<ShellViewModel>();
            var store = host.Services.GetRequiredService<SettingsStore>();
            await shell.InitializeAsync();
            shell.Host.PortText = port.ToString();
            shell.Host.Enabled = true;
            await WaitForAsync(() => Task.FromResult(shell.Host.Status.StartsWith("Listening")), "the server starts");
            Assert.True(shell.Host.HasFingerprint);
            Assert.True(File.Exists(Path.Combine(root, "Config", "Identity", "server-certificate.pem")));
            using var client = Plain(port, null);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/languages")).StatusCode);            // no password: open

            shell.Host.GeneratePasswordCommand.Execute(null);
            Assert.Matches("^[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{4}$", shell.Host.Password);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/languages")).StatusCode);  // at once, without a restart
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", shell.Host.Password);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/languages")).StatusCode);

            shell.Host.ClearPasswordCommand.Execute(null);
            using var open = Plain(port, null);
            Assert.Equal(HttpStatusCode.OK, (await open.GetAsync("/v1/languages")).StatusCode);

            shell.Host.Name = "Pantry";
            var id = shell.Host.ServerId;
            await SettledAsync(shell, store, saved => saved.HostEnabled && saved.HostPort == port && saved.HostName == "Pantry" && saved.HostId == id && saved.HostPassword == "");
            shell.Host.Enabled = false;
            await WaitForAsync(() => Task.FromResult(shell.Host.Status == "The server is off."), "the status says it is off");
            await WaitForAsync(() => RefusesAsync(port), "switching it off releases the port");
            await SettledAsync(shell, store, saved => !saved.HostEnabled);
            shell.Host.StopForExit();
        }
        finally { Loc.Instance.SetLanguage(before); TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task OnTheNetworkEveryConnectionIsEncryptedAndTheServerAnnouncesItself()
    {
        var root = NewRoot(); var port = FreePort(); var beaconPort = FreeUdpPort(); var before = Loc.Instance.Language;
        try
        {
            using var host = App.App.CreateHost(root);
            var shell = host.Services.GetRequiredService<ShellViewModel>();
            var store = host.Services.GetRequiredService<SettingsStore>();
            await shell.InitializeAsync();
            shell.Host.BeaconPort = beaconPort;
            await using var listener = new BeaconListener(beaconPort, TimeSpan.FromSeconds(2));
            Assert.True(listener.Start());
            shell.Host.Name = "Kitchen";
            shell.Host.PortText = port.ToString();
            shell.Host.Password = "k7m2-pq9x-w4hd";
            shell.Host.Enabled = true;
            await WaitForAsync(() => Task.FromResult(shell.Host.Status.StartsWith("Listening")), "the server starts");
            await Task.Delay(300);
            Assert.Empty(listener.Servers);                                       // this computer only: nothing is announced

            shell.Host.AllowNetwork = true;
            await WaitForAsync(() => Task.FromResult(shell.Host.Status.StartsWith("Listening on this computer and on the network")), "the network is allowed");
            await WaitForAsync(() => Task.FromResult(listener.Servers.Count == 1), "the server is announced");
            var heard = listener.Servers.Single().Announcement;
            Assert.Equal("Kitchen", heard.Name);
            Assert.Equal(port, heard.Port);
            Assert.True(heard.Tls);
            Assert.True(heard.PasswordRequired);
            Assert.Equal("Studio", heard.Edition);
            Assert.True(ServerIdentity.Same(heard.Fingerprint, shell.Host.Fingerprint));

            using var plain = Plain(port, null);
            using var refused = await plain.GetAsync("/v1/health");
            Assert.Equal(HttpStatusCode.UpgradeRequired, refused.StatusCode);       // no plain HTTP on a network server
            var probe = await RemoteServerClient.ProbeAsync(new Uri($"https://127.0.0.1:{port}"), default);
            Assert.True(probe.Health.Encrypted);

            shell.Host.Password = "";                                               // a password change reaches the announcement
            await WaitForAsync(() => Task.FromResult(!listener.Servers.Single().Announcement.PasswordRequired), "the announcement follows the password");

            shell.Host.Enabled = false;
            await WaitForAsync(() => Task.FromResult(listener.Servers.Count == 0), "switching it off stops the announcements");
            await SettledAsync(shell, store, saved => !saved.HostEnabled && saved.HostAllowNetwork);
            shell.Host.StopForExit();
        }
        finally { Loc.Instance.SetLanguage(before); TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task ANewIdentityMakesClientsThatTrustedTheOldOneRefuse()
    {
        var root = NewRoot(); var port = FreePort(); var before = Loc.Instance.Language;
        try
        {
            using var host = App.App.CreateHost(root);
            var shell = host.Services.GetRequiredService<ShellViewModel>();
            var store = host.Services.GetRequiredService<SettingsStore>();
            await shell.InitializeAsync();
            shell.Host.PortText = port.ToString();
            shell.Host.Enabled = true;
            await WaitForAsync(() => Task.FromResult(shell.Host.Status.StartsWith("Listening")), "the server starts");
            var old = (await RemoteServerClient.ProbeAsync(new Uri($"https://127.0.0.1:{port}"), default)).Fingerprint!;

            await shell.Host.NewIdentityCommand.ExecuteAsync(null);
            await WaitForAsync(() => Task.FromResult(shell.Host.Status.StartsWith("Listening")), "the server is back");
            var fresh = (await RemoteServerClient.ProbeAsync(new Uri($"https://127.0.0.1:{port}"), default)).Fingerprint!;
            Assert.False(ServerIdentity.Same(old, fresh));
            Assert.True(ServerIdentity.Same(fresh, shell.Host.Fingerprint));
            using var trustingTheOld = new RemoteServerClient(new Uri($"https://127.0.0.1:{port}"), null, old);
            Assert.True((await Assert.ThrowsAsync<RemoteException>(() => trustingTheOld.InfoAsync(default))).IsIdentityChanged);

            shell.Host.Enabled = false;
            await SettledAsync(shell, store, saved => !saved.HostEnabled);
            shell.Host.StopForExit();
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
            shell.Host.PortText = "80";
            shell.Host.Enabled = true;
            await WaitForAsync(() => Task.FromResult(shell.Host.Status == "The port must be a number from 1024 to 65535."), "the port is refused");

            using var taken = new TcpListener(IPAddress.Loopback, port);
            taken.Start();
            shell.Host.PortText = port.ToString();
            await WaitForAsync(() => Task.FromResult(shell.Host.Status.StartsWith("The server could not start")), "the busy port is reported");
            Assert.True(shell.HasError);
            taken.Stop();

            shell.Host.PortText = FreePort().ToString();
            await WaitForAsync(() => Task.FromResult(shell.Host.Status.StartsWith("Listening")), "a free port works");
            var chosen = shell.Host.Port;
            await SettledAsync(shell, store, saved => saved.HostPort == chosen);
            shell.Host.StopForExit();
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
            var store = host.Services.GetRequiredService<SettingsStore>();
            await store.SaveAsync(new(HostEnabled: true, HostPort: port));
            var shell = host.Services.GetRequiredService<ShellViewModel>();
            await shell.InitializeAsync();
            await WaitForAsync(() => Task.FromResult(shell.Host.Status.StartsWith("Listening")), "the server starts");
            shell.Language = "de";
            Assert.StartsWith("Hört nur auf diesem Computer:", shell.Host.Status);
            shell.Language = "fr";
            Assert.StartsWith("À l'écoute sur cet ordinateur uniquement :", shell.Host.Status);
            shell.Language = "en";
            Assert.StartsWith("Listening on this computer only", shell.Host.Status);
            await SettledAsync(shell, store, saved => saved.Language == "en");
            shell.Host.StopForExit();
        }
        finally { Loc.Instance.SetLanguage(before); TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task APortOutsideTheRangeOrMissingTextInTheFileIsSetRightWhenSettingsAreRead()
    {
        var root = NewRoot();
        try
        {
            using var host = App.App.CreateHost(root);
            var store = host.Services.GetRequiredService<SettingsStore>();
            await store.SaveAsync(new(HostPort: 22, HostPassword: null!, HostName: null!, HostId: null!));
            var loaded = await store.LoadAsync();
            Assert.Equal(8642, loaded.HostPort);
            Assert.Equal("", loaded.HostPassword);
            Assert.Equal("", loaded.HostName);
            Assert.False(loaded.HostEnabled);
            Assert.False(loaded.HostAllowNetwork);
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public void TheExampleCommandsNameTheAddressAndTheTlsFlagOnlyWhereItIsNeeded()
    {
        var root = NewRoot();
        try
        {
            using var host = App.App.CreateHost(root);
            var shell = host.Services.GetRequiredService<ShellViewModel>();
            Assert.DoesNotContain("Authorization", shell.Host.Example);                  // no password: no header
            Assert.DoesNotContain("-k", shell.Host.Example);
            shell.Host.Password = "k7m2-pq9x-w4hd";
            Assert.Contains("Authorization: Bearer k7m2-pq9x-w4hd", shell.Host.Example);
            Assert.Contains("http://127.0.0.1:8642/v1/transcriptions", shell.Host.Example);
            Assert.Contains("/v1/audio/transcriptions", shell.Host.Example);
            shell.Host.AllowNetwork = true;
            Assert.Contains("curl.exe -k", shell.Host.Example);                          // the certificate is the server's own
            Assert.Contains("https://", shell.Host.Example);
        }
        finally { TestCleanup.Delete(root); }
    }

    private static int FreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }
}
