using System.IO;
using Microsoft.Extensions.DependencyInjection;
using TriAsr.App;
using TriAsr.Application;
using TriAsr.Infrastructure;

namespace TriAsr.Ui.SmokeTests;

/// <summary>The three editions (ADR-0014): which one runs, what each is called and where it keeps its data, and that the client carries no engines.</summary>
public sealed class EditionTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));

    /// <summary>Runs a test as another edition and puts Studio back, whatever happens.</summary>
    private sealed class As : IDisposable
    {
        private readonly AppEdition _before = Edition.Current;
        public As(AppEdition edition) => Edition.Current = edition;
        public void Dispose() => Edition.Current = _before;
    }

    [Theory]
    [InlineData("studio", AppEdition.Studio)]
    [InlineData("Server", AppEdition.Server)]
    [InlineData(" CLIENT \n", AppEdition.Client)]
    [InlineData("", AppEdition.Studio)]
    [InlineData("nonsense", AppEdition.Studio)]
    [InlineData(null, AppEdition.Studio)]
    public void TheMarkerTextNamesTheEditionAndAnythingElseIsStudio(string? text, AppEdition expected) => Assert.Equal(expected, Edition.Parse(text));

    [Fact]
    public void EachEditionHasItsOwnNameAndItsOwnFolders()
    {
        using (new As(AppEdition.Studio)) { Assert.Equal("Mockingbird Studio", AppInfo.Name); Assert.Equal("TriASR", Edition.DataFolderName); Assert.Equal("TriASR", Edition.InstallFolderName); }
        using (new As(AppEdition.Server)) { Assert.Equal("Mockingbird Server", AppInfo.Name); Assert.Equal("TriASR-Server", Edition.DataFolderName); Assert.StartsWith("Mockingbird Server · v", AppInfo.WindowTitle); }
        using (new As(AppEdition.Client)) { Assert.Equal("Mockingbird Client", AppInfo.Name); Assert.Equal("TriASR-Client", Edition.DataFolderName); }
    }

    [Fact]
    public void WhatAnEditionCanDoFollowsFromWhatItIs()
    {
        using (new As(AppEdition.Studio)) { Assert.True(Edition.RunsEngines); Assert.True(Edition.CanHost); Assert.True(Edition.CanConnect); }
        using (new As(AppEdition.Server)) { Assert.True(Edition.RunsEngines); Assert.True(Edition.CanHost); Assert.False(Edition.CanConnect); }
        using (new As(AppEdition.Client)) { Assert.False(Edition.RunsEngines); Assert.False(Edition.CanHost); Assert.True(Edition.CanConnect); }
    }

    [Fact]
    public void ATextThatNamesStudioNamesTheEditionThatIsRunning()
    {
        var before = Loc.Instance.Language;
        try
        {
            Assert.Equal("Set up Mockingbird Studio", Loc.T("Set up Mockingbird Studio"));
            using (new As(AppEdition.Server))
            {
                Assert.Equal("Set up Mockingbird Server", Loc.T("Set up Mockingbird Studio"));
                Assert.Equal("Mockingbird Server is missing a file", Loc.T("Mockingbird Studio is missing a file"));
                Loc.Instance.SetLanguage("de");
                Assert.DoesNotContain("Studio", Loc.T("Set up Mockingbird Studio"));
                Assert.Contains("Mockingbird Server", Loc.T("Set up Mockingbird Studio"));
                Assert.DoesNotContain("Studio", Loc.Describe("Mockingbird Studio is a x64 program running under emulation."));
            }
            Loc.Instance.SetLanguage("de");
            Assert.Contains("Mockingbird Studio", Loc.T("Set up Mockingbird Studio"));
        }
        finally { Loc.Instance.SetLanguage(before); }
    }

    [Fact]
    public async Task OnlyAServerStartsHostingOnItsFirstStart()
    {
        var root = NewRoot();
        try
        {
            var store = new SettingsStore(new StoragePaths(root), Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsStore>.Instance);
            Assert.False((await store.LoadAsync()).HostEnabled);
            using (new As(AppEdition.Server))
            {
                var settings = await store.LoadAsync();
                Assert.True(settings.HostEnabled);
                Assert.True(settings.HostAllowNetwork);
                Assert.Equal("", settings.HostPassword); // open until the owner chooses a password
            }
            // Once the owner has chosen, the choice stands.
            await store.SaveAsync(new(HostEnabled: false));
            using (new As(AppEdition.Server)) Assert.False((await store.LoadAsync()).HostEnabled);
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public void EachEditionLooksForItsOwnUpdates()
    {
        var studio = UpdateOptions.ForEdition("Studio");
        Assert.Equal(UpdateOptions.GitHub, studio);
        Assert.EndsWith("/download/latest.json", studio.ManifestUri.AbsolutePath);

        var server = UpdateOptions.ForEdition("Server");
        Assert.EndsWith("/download/latest-server.json", server.ManifestUri.AbsolutePath);
        Assert.Equal(UpdateOptions.GitHub.AllowedHost, server.AllowedHost);
        Assert.Equal(UpdateOptions.GitHub.AllowedPathPrefix, server.AllowedPathPrefix);
        Assert.False(server.AllowLoopbackHttp);
        Assert.EndsWith("/download/latest-client.json", UpdateOptions.ForEdition("Client").ManifestUri.AbsolutePath);
    }

    [Fact]
    public void AnInstallerKeepsTheNameOfItsEdition()
    {
        Assert.Equal("Server", UpdateOptions.ForEdition("Server").Edition);
        Assert.Equal("Studio", UpdateOptions.GitHub.Edition);
    }

    [Fact]
    public void ThePathsOfAnEditionAreUnderItsOwnFolder()
    {
        using var server = new As(AppEdition.Server);
        var location = new RuntimePaths(Path.Combine(Path.GetTempPath(), "nowhere")).DefaultDataRoot;
        Assert.EndsWith(Path.DirectorySeparatorChar + "TriASR-Server", location);
    }

    [Fact]
    public async Task TheClientHasNoEnginesAndRemembersItsThemeAndLanguage()
    {
        var root = NewRoot(); var before = Loc.Instance.Language;
        try
        {
            using var edition = new As(AppEdition.Client);
            using (var host = App.App.CreateHost(root))
            {
                Assert.Null(host.Services.GetService<TranscriptionPipeline>());
                Assert.Null(host.Services.GetService<ShellViewModel>());
                Assert.Null(host.Services.GetService<RuntimePaths>());
                var client = host.Services.GetRequiredService<ClientViewModel>();
                await client.InitializeAsync();
                Assert.False(client.LanguageChosen);
                Assert.Equal("Not connected to a server", client.Status);
                client.ChooseLanguage("fr");
                client.SelectedTheme = "Dark";
                var store = host.Services.GetRequiredService<SettingsStore>();
                await WaitForAsync(async () => (await store.LoadAsync()) is { Language: "fr", Theme: "Dark" });
                Assert.Equal("Non connecté à un serveur", client.Status);
                await client.WhenSavedAsync();
                client.Close();
            }
            using (var again = App.App.CreateHost(root))
            {
                var client = again.Services.GetRequiredService<ClientViewModel>();
                await client.InitializeAsync();
                Assert.True(client.LanguageChosen);
                Assert.Equal("fr", client.Language);
                Assert.Equal("Dark", client.SelectedTheme);
                client.Close();
            }
        }
        finally { Loc.Instance.SetLanguage(before); TestCleanup.Delete(root); }
    }

    [Fact]
    public void TheClientShowsAProblemAndLetsItBeDismissed()
    {
        var root = NewRoot();
        try
        {
            using var edition = new As(AppEdition.Client);
            using var host = App.App.CreateHost(root);
            var client = host.Services.GetRequiredService<ClientViewModel>();
            Assert.False(client.HasError);
            client.ReportError("Could not cancel the recording", "The server said no.");
            Assert.True(client.HasError);
            Assert.Equal("Could not cancel the recording", client.ErrorTitle);
            client.DismissErrorCommand.Execute(null);
            Assert.False(client.HasError);
            client.Close();
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public void TheServerAndStudioCarryTheEngines()
    {
        foreach (var kind in new[] { AppEdition.Studio, AppEdition.Server })
        {
            var root = NewRoot();
            try
            {
                using var edition = new As(kind);
                using var host = App.App.CreateHost(root);
                Assert.NotNull(host.Services.GetService<TranscriptionPipeline>());
                Assert.NotNull(host.Services.GetService<RuntimePaths>());
                Assert.Null(host.Services.GetService<ClientViewModel>());
            }
            finally { TestCleanup.Delete(root); }
        }
    }

    private sealed class AnswersYes : IServerDialogs
    {
        public Task<bool> ConfirmTrustAsync(TrustRequest request) => Task.FromResult(true);
        public Task<PasswordAnswer?> AskPasswordAsync(string serverName, bool wrongBefore) => Task.FromResult<PasswordAnswer?>(new(ApiTestData.Key, false));
    }

    [Fact]
    public async Task TheClientConnectsToAServerShowsItsPagesAndSaysSoInTheStatusLine()
    {
        var root = NewRoot(); var before = Loc.Instance.Language;
        await using var api = await Harness.StartAsync();
        try
        {
            using var edition = new As(AppEdition.Client);
            var paths = new StoragePaths(root);
            var client = new ClientViewModel(new SettingsStore(paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsStore>.Instance), new ThemeManager(), paths,
                new UpdateService(UpdateOptions.ForEdition("Client")), Microsoft.Extensions.Logging.Abstractions.NullLogger<ClientViewModel>.Instance) { Dialogs = new AnswersYes() };
            var statuses = new List<string>();
            client.PropertyChanged += (_, change) => { if (change.PropertyName == nameof(ClientViewModel.Status)) statuses.Add(client.Status); };
            Assert.False(client.Servers.HasConnection);

            client.Servers.AddressText = $"127.0.0.1:{api.Server.Port}";
            await client.Servers.AddCommand.ExecuteAsync(null);
            Assert.NotNull(await client.Servers.ConnectAsync(client.Servers.Servers[0]));
            Assert.True(client.Servers.HasConnection);
            await WaitForAsync(() => Task.FromResult(client.Remote.IsConnected));
            Assert.Equal("Connected to Test server", client.Status);
            Assert.Contains("Connected to Test server", statuses);

            Loc.Instance.SetLanguage("de");
            Assert.Equal("Verbunden mit Test server", client.Status);

            client.Servers.DisconnectCommand.Execute(null);
            await WaitForAsync(() => Task.FromResult(!client.Remote.IsConnected));
            Assert.False(client.Servers.HasConnection);
            Assert.Equal("Mit keinem Server verbunden", client.Status);
            client.Close();
        }
        finally { Loc.Instance.SetLanguage(before); TestCleanup.Delete(root); }
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline) { if (await condition()) return; await Task.Delay(50); }
        throw new TimeoutException("Not reached.");
    }
}
