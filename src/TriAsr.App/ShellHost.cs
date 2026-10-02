using System.IO;
using System.Text.Json;
using TriAsr.Domain;
using TriAsr.Engine.Canary;

namespace TriAsr.App;

/// <summary>The side of hosting that needs this window's services: the transcription pipeline, the stored projects and the models (ADR-0013, ADR-0014).</summary>
public sealed partial class ShellViewModel
{
    /// <summary>The name of the sidebar page that shows the server this window is connected to. The page is listed only while there is a connection.</summary>
    public static readonly string RemoteServerPage = Loc.Key("Remote server");

    private HostViewModel? _host;
    private ServerBrowserViewModel? _servers;
    private RemoteWorkspaceViewModel? _remote;
    private Action<TranscriptionJob>? _updateJob;

    /// <summary>Whether the right-hand panel (servers on the network, the server this computer hosts) is open. It also closes by itself when the window is narrow.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _showServerPanel = true;

    /// <summary>Hosting this computer's transcription for other computers and programs.</summary>
    public HostViewModel Host => _host ??= MakeHost();

    private HostViewModel MakeHost()
    {
        var host = new HostViewModel(storage.Root, "Studio", AppInfo.Version, CreateApiService, (title, message) => OnUi(() => ReportError(title, message)), OnUi, logger);
        host.SettingsChanged += () => { if (_initialized) Persist(); };
        return host;
    }

    /// <summary>The questions a connection asks (trust this fingerprint, enter the password). The window sets the real dialogs; until then nothing is trusted.</summary>
    public IServerDialogs? Dialogs { get; set; }

    /// <summary>The servers on the network, and the connection to one of them.</summary>
    public ServerBrowserViewModel Servers => _servers ??= MakeServers();

    /// <summary>The pages of the connected server (send, projects, review).</summary>
    public RemoteWorkspaceViewModel Remote => _remote ??= MakeRemote();

    private ServerBrowserViewModel MakeServers()
    {
        var servers = new ServerBrowserViewModel(new TriAsr.Infrastructure.SavedServerStore(Path.Combine(storage.Root, "Config", "servers.json")), new ForwardedDialogs(() => Dialogs), OnUi);
        servers.ConnectionChanged += connection => OnUi(() =>
        {
            Remote.Attach(connection);
            if (connection is not null) SelectedPage = Navigation.First(page => page.Name == RemoteServerPage);
            else if (IsRemotePage) SelectedPage = Navigation[0];
        });
        return servers;
    }

    private RemoteWorkspaceViewModel MakeRemote()
    {
        var remote = new RemoteWorkspaceViewModel(OnUi, (title, message) => OnUi(() => ReportError(title, message)), Path.Combine(storage.Root, "Temp", "Remote"));
        remote.ConnectionLost += reason => OnUi(() => Servers.Lost(reason));
        remote.ReviewSaved += () => OnUi(() => Status = T("Edits saved with revision history"));
        return remote;
    }

    /// <summary>Passes the connection's questions on to whatever dialogs the window has set; with none, the answer is no.</summary>
    private sealed class ForwardedDialogs(Func<IServerDialogs?> current) : IServerDialogs
    {
        public Task<bool> ConfirmTrustAsync(TrustRequest request) => current() is { } dialogs ? dialogs.ConfirmTrustAsync(request) : Task.FromResult(false);
        public Task<PasswordAnswer?> AskPasswordAsync(string serverName, bool wrongBefore) => current() is { } dialogs ? dialogs.AskPasswordAsync(serverName, wrongBefore) : Task.FromResult<PasswordAnswer?>(null);
    }

    private string IncomingFolder => Path.Combine(storage.Root, "Api", "Incoming");

    private ApiService CreateApiService(HostViewModel host) => new(new ApiServiceDependencies(queue, pipeline, repository,
        (id, token) => stages.LoadReviewAsync(id, token), IncomingFolder, Path.Combine(storage.Root, "Api", "Exports"), AppInfo.Version,
        () => host.Password,
        language => { var missing = Array.Empty<string>(); OnUi(() => missing = MissingRequiredModelsFor(language)); return missing; },
        () => !IsModelBusy && !IsBenchmarking && !SetupRunning && !IsCheckingSystem,
        busy => OnUi(() => host.IsBusy = busy),
        () => host.DisplayName, "Studio", LoadReviewBundleAsync, (transcript, token) => stages.SaveManualAsync(transcript, token), AudioPathFor));

    private bool IsApiJob(TranscriptionJob job) => job.SourcePath.StartsWith(IncomingFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>The finished transcript as the review page shows it: as edited, as the programs wrote it, and the raw engine texts.</summary>
    private async Task<ReviewBundle> LoadReviewBundleAsync(Guid id, CancellationToken token)
    {
        var review = TriAsr.Fusion.TranscriptQuality.FlagRepetition(await stages.LoadReviewAsync(id, token));
        var automatic = await stages.LoadFinalAsync(id, token);
        var directory = workspace.DirectoryFor(id);
        var whisperPath = Path.Combine(directory, "whisper.json");
        var whisper = File.Exists(whisperPath) ? JsonSerializer.Deserialize<EngineTranscript>(await File.ReadAllTextAsync(whisperPath, token)) : null;
        var canaryPath = Path.Combine(directory, "canary.json");
        var canary = ""; string? note = null;
        if (File.Exists(canaryPath)) canary = JsonSerializer.Deserialize<CanaryNative.Result>(await File.ReadAllTextAsync(canaryPath, token))?.Transcript.Text ?? "";
        else note = File.Exists(Path.Combine(directory, "Canary", "skipped-language.json")) ? "outside-coverage" : "incomplete";
        return new ReviewBundle(review, automatic, whisper?.Text ?? "", canary, note);
    }

    private string? AudioPathFor(Guid id, string kind)
    {
        var directory = workspace.DirectoryFor(id);
        var normalized = Path.Combine(directory, "normalized.wav");
        var playback = Path.Combine(directory, "playback.m4a");
        if (kind == "normalized") return File.Exists(normalized) ? normalized : null;
        return File.Exists(playback) ? playback : File.Exists(normalized) ? normalized : null;
    }
}
