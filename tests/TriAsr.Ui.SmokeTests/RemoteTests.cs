using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using TriAsr.App;
using TriAsr.Domain;
using TriAsr.Infrastructure;

namespace TriAsr.Ui.SmokeTests;

/// <summary>Connecting to servers and working with one: the list, the questions a first connection asks, remembered servers, and the pages of a connected server.</summary>
public sealed class RemoteTests
{
    private sealed class ScriptedDialogs : IServerDialogs
    {
        public bool Trust { get; set; } = true;
        public List<string?> Passwords { get; } = [];
        public bool RememberPassword { get; set; }
        public List<TrustRequest> TrustAsked { get; } = [];
        public List<bool> WrongFlags { get; } = [];
        public bool Forbidden { get; set; }
        public Task<bool> ConfirmTrustAsync(TrustRequest request)
        {
            Assert.False(Forbidden, "a remembered server must not ask to be trusted again");
            TrustAsked.Add(request);
            return Task.FromResult(Trust);
        }
        public Task<PasswordAnswer?> AskPasswordAsync(string serverName, bool wrongBefore)
        {
            Assert.False(Forbidden, "a remembered password must not be asked again");
            WrongFlags.Add(wrongBefore);
            if (Passwords.Count == 0) return Task.FromResult<PasswordAnswer?>(null);
            var next = Passwords[0]; Passwords.RemoveAt(0);
            return Task.FromResult(next is null ? null : new PasswordAnswer(next, RememberPassword));
        }
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));

    private static ServerBrowserViewModel Browser(string root, ScriptedDialogs dialogs, int beaconPort = 0, TimeSpan? lifetime = null) =>
        new(new SavedServerStore(Path.Combine(root, "servers.json")), dialogs, action => action(), beaconPort == 0 ? FreeUdpPort() : beaconPort, lifetime);

    private static int FreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private static string AddressOf(Harness api) => $"127.0.0.1:{api.Server.Port}";

    private static async Task<T> EventuallyAsync<T>(Func<T?> read, string because) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline) { if (read() is { } value) return value; await Task.Delay(30); }
        throw new TimeoutException("Not reached: " + because);
    }

    // ---- typing an address -------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("192.168.1.20", "https://192.168.1.20:8642/", "http://192.168.1.20:8642/")]
    [InlineData(" kitchen ", "https://kitchen:8642/", "http://kitchen:8642/")]
    [InlineData("kitchen:9000", "https://kitchen:9000/", "http://kitchen:9000/")]
    [InlineData("[::1]:8642", "https://[::1]:8642/", "http://[::1]:8642/")]
    [InlineData("https://kitchen:1234/some/path", "https://kitchen:1234/", null)]
    [InlineData("http://10.0.0.5:7000", "http://10.0.0.5:7000/", null)]
    public void WhatIsTypedBecomesTheAddressesToTry(string typed, string first, string? second)
    {
        var candidates = ServerBrowserViewModel.Candidates(typed).Select(uri => uri.ToString()).ToArray();
        Assert.Equal(second is null ? [first] : [first, second], candidates);
    }

    [Theory]
    [InlineData("")] [InlineData("   ")] [InlineData("ftp://host")] [InlineData("http://")] [InlineData("a b c")]
    public void NonsenseIsNotAnAddress(string typed) => Assert.Empty(ServerBrowserViewModel.Candidates(typed));

    // ---- connecting ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFirstConnectionShowsTheFingerprintAsksThePasswordAndRemembersWhatTheUserAllowed()
    {
        var root = NewRoot();
        await using var api = await Harness.StartAsync();
        try
        {
            var dialogs = new ScriptedDialogs { Passwords = { ApiTestData.Key }, RememberPassword = true };
            await using var browser = Browser(root, dialogs);
            browser.Start();
            browser.AddressText = AddressOf(api);
            await browser.AddCommand.ExecuteAsync(null);
            var entry = Assert.Single(browser.Servers);
            Assert.Equal("Test server", entry.Name);
            Assert.True(entry.PasswordRequired);
            Assert.Same(entry, browser.Selected);
            Assert.Equal("Found Test server. Choose Connect.", browser.Status);

            RemoteConnection? announced = null;
            browser.ConnectionChanged += connection => announced = connection;
            var connection = await browser.ConnectAsync(entry);
            Assert.NotNull(connection);
            Assert.Same(connection, announced);
            Assert.Single(dialogs.TrustAsked);
            Assert.True(dialogs.TrustAsked[0].Encrypted);
            Assert.False(dialogs.TrustAsked[0].Changed);
            Assert.True(ServerIdentity.Same(dialogs.TrustAsked[0].Fingerprint, api.Identity.Fingerprint));
            Assert.Equal([false], dialogs.WrongFlags);
            Assert.True(ServerIdentity.Same(entry.Pinned, api.Identity.Fingerprint));
            Assert.Equal("Connected to Test server", browser.Status);
            Assert.True(browser.HasConnection);
            Assert.True(entry.IsConnected);
            Assert.True(connection!.Info.ModelsReady);

            // What is kept: the fingerprint, and the password only in a protected form.
            var file = await File.ReadAllTextAsync(Path.Combine(root, "servers.json"));
            Assert.Contains(api.Identity.Fingerprint, file, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ApiTestData.Key, file);
            Assert.Contains("protectedPassword", file);

            // A new window (a new start of the program) knows the server and asks nothing.
            var quiet = new ScriptedDialogs { Forbidden = true };
            await using var later = Browser(root, quiet);
            later.Start();
            var known = Assert.Single(later.Servers);
            Assert.True(known.IsSaved);
            var again = await later.ConnectAsync(known);
            Assert.NotNull(again);
            Assert.Equal("Connected to Test server", later.Status);

            browser.Disconnect();
            Assert.Null(browser.Connection);
            Assert.False(entry.IsConnected);
            Assert.Null(announced);
            Assert.Equal("Not connected.", browser.Status);
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task ARefusedFingerprintMeansNothingSecretIsSent()
    {
        var root = NewRoot();
        await using var api = await Harness.StartAsync();
        try
        {
            var dialogs = new ScriptedDialogs { Trust = false, Passwords = { ApiTestData.Key } };
            await using var browser = Browser(root, dialogs);
            browser.AddressText = AddressOf(api);
            await browser.AddCommand.ExecuteAsync(null);
            Assert.Null(await browser.ConnectAsync(browser.Servers[0]));
            Assert.Equal("Not connected: the server was not trusted.", browser.Status);
            Assert.Empty(dialogs.WrongFlags);                         // the password was never asked for
            Assert.Equal("", browser.Servers[0].Pinned);
            Assert.False(File.Exists(Path.Combine(root, "servers.json")));
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task AWrongPasswordIsAskedAgainAndGivingUpLeavesNothingConnected()
    {
        var root = NewRoot();
        await using var api = await Harness.StartAsync();
        try
        {
            var dialogs = new ScriptedDialogs { Passwords = { "wrong", ApiTestData.Key } };
            await using var browser = Browser(root, dialogs);
            browser.AddressText = AddressOf(api);
            await browser.AddCommand.ExecuteAsync(null);
            Assert.NotNull(await browser.ConnectAsync(browser.Servers[0]));
            Assert.Equal([false, true], dialogs.WrongFlags);          // the second time it says the last one was refused
            browser.Disconnect();

            var giveUp = new ScriptedDialogs { Passwords = { null } };
            await using var other = Browser(NewRoot(), giveUp);
            other.AddressText = AddressOf(api);
            await other.AddCommand.ExecuteAsync(null);
            Assert.Null(await other.ConnectAsync(other.Servers[0]));
            Assert.Equal("Not connected: no password was given.", other.Status);
            Assert.False(other.HasConnection);
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task AServerThatShowsAnotherCertificateThanTheTrustedOneIsQuestionedLoudly()
    {
        var root = NewRoot();
        await using var api = await Harness.StartAsync();
        try
        {
            var dialogs = new ScriptedDialogs { Passwords = { ApiTestData.Key } };
            await using var browser = Browser(root, dialogs);
            browser.AddressText = AddressOf(api);
            await browser.AddCommand.ExecuteAsync(null);
            var entry = browser.Servers[0];
            Assert.NotNull(await browser.ConnectAsync(entry));
            browser.Disconnect();

            entry.Pinned = new string('C', 64);                       // what was trusted is no longer what the server shows
            dialogs.Trust = false;
            Assert.Null(await browser.ConnectAsync(entry));
            Assert.True(dialogs.TrustAsked[^1].Changed);
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task AServerNobodyAnswersIsReportedAndForgettingRemovesWhatWasKept()
    {
        var root = NewRoot();
        await using var api = await Harness.StartAsync();
        try
        {
            var dialogs = new ScriptedDialogs { Passwords = { ApiTestData.Key }, RememberPassword = true };
            await using var browser = Browser(root, dialogs);
            browser.AddressText = AddressOf(api);
            await browser.AddCommand.ExecuteAsync(null);
            var entry = browser.Servers[0];
            Assert.NotNull(await browser.ConnectAsync(entry));
            browser.Disconnect();

            browser.Selected = entry;
            browser.ForgetCommand.Execute(null);
            Assert.Equal("", entry.Pinned);
            Assert.Null(entry.Password);
            var kept = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "servers.json"))).RootElement;
            Assert.Equal(0, kept.GetArrayLength());

            var gone = new TcpListener(IPAddress.Loopback, 0); gone.Start();
            var port = ((IPEndPoint)gone.LocalEndpoint).Port; gone.Stop();
            browser.AddressText = $"127.0.0.1:{port}";
            await browser.AddCommand.ExecuteAsync(null);
            Assert.StartsWith($"No server answered at 127.0.0.1:{port}", browser.Status);
            browser.AddressText = "not an address at all";
            await browser.AddCommand.ExecuteAsync(null);
            Assert.Equal("That address is not valid.", browser.Status);
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task AServerAnnouncingItselfAppearsInTheListAndIsForgottenWhenItFallsSilent()
    {
        var root = NewRoot(); var port = FreeUdpPort();
        try
        {
            var dialogs = new ScriptedDialogs();
            await using var browser = Browser(root, dialogs, port, TimeSpan.FromMilliseconds(900));
            browser.Start();
            Assert.True(browser.IsEmpty);
            var announcement = new ServerAnnouncement("srv-1", "Kitchen", 8642, true, true, "Server", "1.2.3", new string('A', 64));
            await using (var sender = new BeaconSender(() => announcement, port, TimeSpan.FromMilliseconds(100), () => [new IPEndPoint(IPAddress.Loopback, port)]))
            {
                sender.Start();
                var entry = await EventuallyAsync(() => browser.Servers.FirstOrDefault(item => item.Id == "srv-1"), "the server is listed");
                Assert.Equal("Kitchen", entry.Name);
                Assert.True(entry.PasswordRequired);
                Assert.True(entry.IsOnline);
                Assert.Contains("this computer", entry.Detail);
                Assert.Contains("encrypted", entry.Detail);
                Assert.Contains("password", entry.Detail);
                announcement = announcement with { Name = "Pantry", PasswordRequired = false };
                await EventuallyAsync(() => browser.Servers.FirstOrDefault(item => item.Name == "Pantry"), "a rename shows");
            }
            await EventuallyAsync(() => browser.Servers.Count == 0 ? browser : null, "a silent server leaves the list");
            Assert.True(browser.IsEmpty);
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public void TheEntriesAreDescribedInTheLanguage()
    {
        var before = Loc.Instance.Language;
        try
        {
            var entry = new ServerEntry("x", "Kitchen", new Uri("https://192.168.1.20:8642")) { PasswordRequired = true, IsOnline = true, Edition = "Server" };
            Loc.Instance.SetLanguage("de");
            Assert.Equal("192.168.1.20:8642 · Server · verschlüsselt · Passwort", entry.Detail);
            Loc.Instance.SetLanguage("en");
            Assert.Equal("192.168.1.20:8642 · Server · encrypted · password", entry.Detail);
        }
        finally { Loc.Instance.SetLanguage(before); }
    }

    // ---- working with a connected server -----------------------------------------------------------------------------------------------

    private static async Task<(RemoteWorkspaceViewModel Workspace, ServerBrowserViewModel Browser)> ConnectedAsync(string root, Harness api, List<(string, string)>? errors = null)
    {
        var dialogs = new ScriptedDialogs { Passwords = { ApiTestData.Key } };
        var browser = Browser(root, dialogs);
        browser.AddressText = AddressOf(api);
        await browser.AddCommand.ExecuteAsync(null);
        var connection = await browser.ConnectAsync(browser.Servers[0]) ?? throw new InvalidOperationException(browser.Status);
        var workspace = new RemoteWorkspaceViewModel(action => action(), (title, message) => errors?.Add((title, message)), Path.Combine(root, "remote"));
        workspace.Attach(connection);
        return (workspace, browser);
    }

    [Fact]
    public async Task ARecordingIsSentFollowedReviewedEditedAndExported()
    {
        var root = NewRoot();
        await using var api = await Harness.StartAsync();
        var (workspace, browser) = await ConnectedAsync(root, api);
        await using var _ = browser;
        using var __ = workspace;
        try
        {
            Assert.True(workspace.IsConnected);
            Assert.Equal("Test server", workspace.ServerName);
            Assert.Equal("", workspace.ServerNote);
            Assert.False(workspace.CanSend);                           // no recording chosen yet
            var recording = Path.Combine(root, "meeting.wav");
            await File.WriteAllBytesAsync(recording, new byte[200_000]);
            workspace.SourcePath = recording;
            workspace.SelectedLanguage = "de";
            Assert.True(workspace.CanSend);
            await workspace.SendCommand.ExecuteAsync(null);
            Assert.Equal("Projects", workspace.SelectedTab);
            Assert.Equal("Sent. The server is working on it.", workspace.SendStatus);
            Assert.Equal("", workspace.SourcePath);
            var row = Assert.Single(workspace.Jobs);
            Assert.Equal("meeting.wav", row.Name);
            Assert.Same(row, workspace.SelectedJob);

            await EventuallyAsync(() => row.CanOpen ? row : null, "the job completes and the list follows");
            Assert.Equal("Complete", row.StateText);
            workspace.OpenSelectedCommand.NotifyCanExecuteChanged();
            await workspace.OpenSelectedCommand.ExecuteAsync(null);
            Assert.Equal("Review", workspace.SelectedTab);
            Assert.Equal(2, workspace.Regions.Count);
            Assert.Equal("raw whisper", workspace.RawWhisper);
            Assert.Equal("meeting.wav", workspace.ReviewName);
            Assert.Equal("DE · regions: 2 · to listen to: 1", workspace.ReviewSummary);
            Assert.Equal("auto: Guten Tag, meine Damen und Herren.", workspace.Regions[0].MachineText);
            await EventuallyAsync(() => workspace.AudioSource, "the listening copy arrives");
            Assert.True(File.Exists(workspace.NormalizedAudioPath));
            Assert.Equal("", workspace.AudioStatus);

            // Edit one region, restore another's automatic text, save.
            workspace.SelectedRegion = workspace.Regions[0];
            workspace.UseAutomaticCommand.Execute(null);
            Assert.Equal("auto: Guten Tag, meine Damen und Herren.", workspace.Regions[0].Text);
            workspace.Regions[1].Text = "Willkommen zur Sitzung.";
            var saved = false;
            workspace.ReviewSaved += () => saved = true;
            await workspace.SaveReviewCommand.ExecuteAsync(null);
            Assert.True(saved);
            Assert.Equal("auto: Guten Tag, meine Damen und Herren.", api.Saved!.Regions[0].FinalText);
            Assert.Equal("Willkommen zur Sitzung.", api.Saved.Regions[1].FinalText);
            Assert.Equal("manual", workspace.Regions[1].Original.Source);   // the window now shows the saved state

            // Export is made here, from what is on screen.
            var srt = Path.Combine(root, "talk.srt");
            await workspace.ExportAsync(srt);
            Assert.Contains("Willkommen zur Sitzung.", await File.ReadAllTextAsync(srt));
            workspace.SelectedExportMode = "Strict Verbatim";
            var txt = Path.Combine(root, "talk.txt");
            await workspace.ExportAsync(txt);
            Assert.Contains("auto: Guten Tag", await File.ReadAllTextAsync(txt));
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task AServerWithoutModelsSaysSoAndASentRecordingCanBeCancelled()
    {
        var root = NewRoot(); string[] missing = ["Whisper large-v3", "Canary 1B"];
        await using var api = await Harness.StartAsync(configure: h => { h.MissingModels = _ => missing; h.Stages.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); });
        var errors = new List<(string, string)>();
        var (workspace, browser) = await ConnectedAsync(root, api, errors);
        await using var _ = browser;
        using var __ = workspace;
        try
        {
            Assert.StartsWith("This server cannot transcribe yet", workspace.ServerNote);
            Assert.Contains("Whisper large-v3", workspace.ServerNote);
            var recording = Path.Combine(root, "x.wav");
            await File.WriteAllBytesAsync(recording, new byte[1000]);
            workspace.SourcePath = recording;
            Assert.False(workspace.CanSend);                          // not even offered
            await workspace.SendCommand.ExecuteAsync(null);
            Assert.Empty(workspace.Jobs);

            missing = [];                                              // the server is set up meanwhile
            Assert.Equal("", (await EventuallyAsync(() => workspace.ServerNote.Length == 0 ? workspace.ServerNote : null, "the note follows the server")));
            Assert.True(workspace.CanSend);
            await workspace.SendCommand.ExecuteAsync(null);
            var row = Assert.Single(workspace.Jobs);
            await EventuallyAsync(() => row.Job.State == "running" ? row : null, "it starts");
            Assert.True(row.CanCancel);
            await workspace.CancelSelectedCommand.ExecuteAsync(null);
            await EventuallyAsync(() => row.Job.State == "cancelled" ? row : null, "it stops");
            Assert.Equal("Cancelled", row.StateText);
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task WhenTheServerGoesAwayTheWindowIsTold()
    {
        var root = NewRoot();
        var api = await Harness.StartAsync();
        var (workspace, browser) = await ConnectedAsync(root, api);
        await using var _ = browser;
        using var __ = workspace;
        try
        {
            string? lost = null;
            workspace.ConnectionLost += reason => lost = reason;
            await api.DisposeAsync();
            await EventuallyAsync(() => lost, "the loss is noticed");
            browser.Lost(lost!);
            Assert.False(browser.HasConnection);
            Assert.StartsWith("The connection was lost:", browser.Status);
        }
        finally { TestCleanup.Delete(root); }
    }
}
