using System.IO;
using Microsoft.Extensions.DependencyInjection;
using TriAsr.App;
using TriAsr.Application;

namespace TriAsr.Ui.SmokeTests;

/// <summary>The link card of Studio's New transcription page and the link helper (ADR-0018), without a network: stand-ins fetch and install.</summary>
public sealed class LinkCardTests
{
    private sealed class FakeTool : ILinkTool
    {
        public bool Installed { get; set; }
        public Exception? Fail { get; set; }
        public bool NewerExists { get; set; }
        public int Installs { get; private set; }
        public TaskCompletionSource? Hold { get; set; }

        public async Task<bool> InstallAsync(IProgress<double>? percent, CancellationToken token)
        {
            Installs++;
            percent?.Report(30);
            if (Hold is { } hold) await hold.Task.WaitAsync(token);
            if (Fail is { } error) throw error;
            if (Installed && !NewerExists) return false;
            Installed = true; NewerExists = false;
            percent?.Report(100);
            return true;
        }

        public Task<bool> UpdateAvailableAsync(CancellationToken token) => Task.FromResult(Installed && NewerExists);
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));

    private static (ShellViewModel Shell, IDisposable Host) Start(string root, FakeLinks links, FakeTool tool)
    {
        var host = App.App.CreateHost(root, services => { services.AddSingleton<ILinkFetcher>(links); services.AddSingleton<ILinkTool>(tool); });
        return (host.Services.GetRequiredService<ShellViewModel>(), host);
    }

    [Fact]
    public async Task ThePastedAddressIsFetchedOnThisComputerAndBecomesTheRecordingToTranscribe()
    {
        var root = NewRoot();
        var links = new FakeLinks { Progress = [50] };
        var (shell, host) = Start(root, links, new FakeTool { Installed = true });
        using var _ = host;
        try
        {
            var card = shell.Link;
            Assert.Equal("Fetch the sound", card.ActionLabel);
            Assert.False(card.RunCommand.CanExecute(null));
            Assert.False(card.HasNote);                                                       // the helper is installed: nothing to say
            card.LinkText = "  media.example/talk.mp3 ";
            Assert.True(card.RunCommand.CanExecute(null));
            await card.RunCommand.ExecuteAsync(null);

            var call = Assert.Single(links.Calls);
            Assert.Equal("https://media.example/talk.mp3", call.Link.AbsoluteUri);
            Assert.True(call.Options.AllowPrivateNetwork);                                    // on the person's own computer a link into their own network is theirs to follow
            Assert.StartsWith(Path.Combine(root, "Links") + Path.DirectorySeparatorChar, call.Folder);
            Assert.Equal(Path.Combine(call.Folder, "Fetched Talk.mp3"), shell.SourcePath);
            Assert.True(File.Exists(shell.SourcePath));
            Assert.Equal("Fetched: Fetched Talk.mp3", card.Status);
            Assert.Equal("", card.LinkText);
            Assert.False(card.IsBusy);
            await shell.Host.DisposeAsync();
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task AnAddressThatIsNotUsableIsExplainedAndNothingIsFetched()
    {
        var root = NewRoot(); var before = Loc.Instance.Language;
        var links = new FakeLinks();
        var (shell, host) = Start(root, links, new FakeTool { Installed = true });
        using var _ = host;
        try
        {
            var card = shell.Link;
            card.LinkText = "file:///C:/Windows/secret.wav";
            await card.RunCommand.ExecuteAsync(null);
            Assert.Equal("Only web addresses (http or https) can be used as links.", card.Status);
            Assert.Empty(links.Calls);
            Assert.Equal("", shell.SourcePath);
            Loc.Instance.SetLanguage("de");
            card.RefreshTexts();
            Assert.Equal("Als Links sind nur Webadressen (http oder https) möglich.", card.Status);
            Assert.Equal("Ton holen", card.ActionLabel);
            await shell.Host.DisposeAsync();
        }
        finally { Loc.Instance.SetLanguage(before); TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task AFailedFetchSaysWhyInTheInterfaceLanguageAndLeavesNoFolderBehind()
    {
        var root = NewRoot(); var before = Loc.Instance.Language;
        var links = new FakeLinks { Fail = _ => new LinkException(LinkMessages.NeedsHelper) };
        var (shell, host) = Start(root, links, new FakeTool { Installed = false });
        using var _ = host;
        try
        {
            var card = shell.Link;
            card.LinkText = "https://video.example/watch?v=1";
            await card.RunCommand.ExecuteAsync(null);
            Assert.Equal(LinkMessages.NeedsHelper, card.Status);
            Assert.Equal("https://video.example/watch?v=1", card.LinkText);                    // kept so that it can be tried again
            Assert.Equal("", shell.SourcePath);

            Loc.Instance.SetLanguage("fr");
            card.RefreshTexts();
            Assert.StartsWith("Cette adresse mène à une page web", card.Status);

            links.Fail = _ => new LinkException(LinkMessages.FailedPrefix + "the site said no");
            await card.RunCommand.ExecuteAsync(null);
            Assert.Equal("Le lien n'a pas pu être téléchargé : the site said no", card.Status);   // the site's own words are kept as they are

            links.Fail = _ => new IOException("The disk is full.");
            await card.RunCommand.ExecuteAsync(null);
            Assert.Equal("Le lien n'a pas pu être téléchargé : The disk is full.", card.Status);
            Assert.True(!Directory.Exists(Path.Combine(root, "Links")) || Directory.GetFileSystemEntries(Path.Combine(root, "Links")).Length == 0);
            await shell.Host.DisposeAsync();
        }
        finally { Loc.Instance.SetLanguage(before); TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task AFetchCanBeCancelledAndTheCardIsReadyAgain()
    {
        var root = NewRoot();
        var links = new FakeLinks { Hold = new TaskCompletionSource() };
        var (shell, host) = Start(root, links, new FakeTool { Installed = true });
        using var _ = host;
        try
        {
            var card = shell.Link;
            card.LinkText = "https://media.example/long.mp3";
            var running = card.RunCommand.ExecuteAsync(null);
            Assert.True(card.IsBusy);
            Assert.Equal("Fetching the sound…", card.Status);
            Assert.False(card.RunCommand.CanExecute(null));                                    // not twice at once
            Assert.True(card.CancelCommand.CanExecute(null));
            card.CancelCommand.Execute(null);
            await running;
            Assert.False(card.IsBusy);
            Assert.Equal("Cancelled", card.Status);
            Assert.Equal("", shell.SourcePath);
            Assert.False(card.CancelCommand.CanExecute(null));
            Assert.True(card.RunCommand.CanExecute(null));
            await shell.Host.DisposeAsync();
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task WithoutTheHelperTheCardOffersToInstallItAndTheOfferGoesAwayOnceItIsThere()
    {
        var root = NewRoot();
        var tool = new FakeTool { Installed = false, Hold = new TaskCompletionSource() };
        var (shell, host) = Start(root, new FakeLinks(), tool);
        using var _ = host;
        try
        {
            var card = shell.Link;
            Assert.True(card.HasNote);
            Assert.StartsWith("The link helper is not installed.", card.Note);
            Assert.Contains("github.com/yt-dlp/yt-dlp", card.Note);                            // where it comes from is said before anything is downloaded
            Assert.Equal("Install the link helper", card.NoteActionLabel);
            Assert.True(card.HasNoteAction);
            Assert.Same(shell.LinkHelper, shell.Host.LinkHelper);                              // the Servers page and the Server window show the same helper

            var installing = shell.LinkHelper.RunCommand.ExecuteAsync(null);
            Assert.True(shell.LinkHelper.IsBusy);
            Assert.False(shell.LinkHelper.RunCommand.CanExecute(null));
            Assert.StartsWith("Downloading the link helper… ", card.Note);
            tool.Hold.SetResult();
            await installing;

            Assert.True(shell.LinkHelper.Installed);
            Assert.Equal("The link helper was installed.", shell.LinkHelper.Summary);
            Assert.False(card.HasNote);
            Assert.False(card.HasNoteAction);
            Assert.Equal("Check for a newer link helper", shell.LinkHelper.ActionLabel);

            tool.Hold = null;
            await shell.LinkHelper.RunCommand.ExecuteAsync(null);
            Assert.Equal("The link helper is up to date.", shell.LinkHelper.Summary);
            tool.NewerExists = true;
            await shell.LinkHelper.RunCommand.ExecuteAsync(null);
            Assert.Equal("The link helper was updated.", shell.LinkHelper.Summary);
            await shell.Host.DisposeAsync();
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task AnInstallationThatFailsSaysWhyAndCanBeTriedAgain()
    {
        var root = NewRoot(); var before = Loc.Instance.Language;
        var tool = new FakeTool { Installed = false, Fail = new LinkException(LinkMessages.HelperChecksum) };
        var (shell, host) = Start(root, new FakeLinks(), tool);
        using var _ = host;
        try
        {
            await shell.LinkHelper.RunCommand.ExecuteAsync(null);
            Assert.False(shell.LinkHelper.Installed);
            Assert.Equal("The link helper could not be installed: " + LinkMessages.HelperChecksum, shell.LinkHelper.Summary);
            Assert.True(shell.LinkHelper.RunCommand.CanExecute(null));
            Assert.True(shell.Link.HasNoteAction);

            Loc.Instance.SetLanguage("es");
            shell.LinkHelper.RefreshTexts();
            Assert.Equal("No se pudo instalar el asistente de enlaces: El asistente de enlaces no superó la suma de verificación y no se instaló.", shell.LinkHelper.Summary);

            tool.Fail = null;
            await shell.LinkHelper.RunCommand.ExecuteAsync(null);
            Assert.True(shell.LinkHelper.Installed);
            Assert.Equal(2, tool.Installs);
            await shell.Host.DisposeAsync();
        }
        finally { Loc.Instance.SetLanguage(before); TestCleanup.Delete(root); }
    }

    [Fact]
    public void EveryLinkSentenceHasAnEntryInEveryLanguageAndIsListedForTheLayersBelow()
    {
        var extra = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(TranslationSources.AppFolder, "Languages", "extra-keys.json")))!;
        foreach (var message in LinkMessages.All.Append(TranscriptionProgressTracker.LinkStage))
        {
            Assert.Contains(message, extra);
            foreach (var code in new[] { "hu", "de", "es", "fr" }) Assert.NotEqual(message, Loc.Load(code)[message]);
        }
        Assert.Contains(LinkMessages.FailedPrefix + "{0}", LowerLayerMessages.Templates);
    }
}
