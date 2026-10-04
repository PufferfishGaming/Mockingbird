using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TriAsr.Domain;
using TriAsr.Infrastructure;

namespace TriAsr.App;

/// <summary>What the watch-folder card needs from whoever does the work: Studio (with its own engines) or the Client (through the connected server).</summary>
public interface IWatchFolderOwner
{
    string WatchFolder { get; }

    /// <summary>Called from the folder picker: watching a folder you just chose is what you asked for, so it also switches watching on.</summary>
    void SetWatchFolder(string path);
}

/// <summary>
/// The Client's watch folder: recordings added to a chosen folder are sent to the connected server one after another, and the transcript the server
/// writes is saved next to the recording, as Studio's watch folder does with its own engines. Without a connection the recordings wait for one.
/// </summary>
public sealed partial class RemoteWatchViewModel : ObservableObject, IWatchFolderOwner, IAsyncDisposable
{
    /// <summary>The choices, kept in the data folder. <see cref="Output"/> holds the English value of <see cref="ShellViewModel.WatchOutputs"/>.</summary>
    public sealed record Choices(string Folder = "", bool Enabled = false, string Language = "auto", string Output = "Text (.txt)", string Speakers = "off");

    private readonly Func<RemoteConnection?> _connection;
    private readonly Func<string> _exportMode;
    private readonly Action<string, string> _reportError;
    private readonly Action<Action> _onUi;
    private readonly Action<RemoteJob> _sent;
    private readonly string _choicesPath, _ledgerPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly WatchOptions? _options;
    private FolderWatcher? _watcher;
    private bool _loading;
    private string _current = "", _secondLanguage = "";
    private IReadOnlyList<LanguageOption>? _languages, _secondLanguages;

    /// <param name="connection">The server the recordings go to, or null while there is none.</param>
    /// <param name="exportMode">How the transcript is written next to the recording: <c>Readable</c> or <c>Strict Verbatim</c>.</param>
    /// <param name="sent">Shows a recording the server took in the project list.</param>
    /// <param name="options">How quickly a new file counts as complete; only a test changes it.</param>
    public RemoteWatchViewModel(Func<RemoteConnection?> connection, Func<string> exportMode, Action<string, string> reportError, Action<Action> onUi, string dataRoot, Action<RemoteJob> sent, WatchOptions? options = null)
    {
        _connection = connection; _exportMode = exportMode; _reportError = reportError; _onUi = onUi; _sent = sent; _options = options;
        _choicesPath = Path.Combine(dataRoot, "Config", "client-watch.json");
        _ledgerPath = Path.Combine(dataRoot, "Config", "client-watch-ledger.json");
        Load();
    }

    public IReadOnlyList<string> WatchOutputs { get; } = [ShellViewModel.WatchAsText, ShellViewModel.WatchAsSubtitles, ShellViewModel.WatchProjectOnly];
    public IReadOnlyList<SpeakerChoice> SpeakerChoices => SpeakerChoice.All;
    public IReadOnlyList<LanguageOption> Languages => _languages ??= SpeechLanguages.First();
    public IReadOnlyList<LanguageOption> WatchSecondLanguages => _secondLanguages ??= SpeechLanguages.Second(WatchLanguage);

    [ObservableProperty] private bool _watchEnabled;
    [ObservableProperty] private string _watchFolder = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanChooseWatchSecondLanguage))] private string _watchLanguage = "auto";
    [ObservableProperty] private string _watchOutput = ShellViewModel.WatchAsText;
    [ObservableProperty] private string _watchSpeakers = "off";
    [ObservableProperty] private string _watchStatus = Loc.T("Off");
    [ObservableProperty] private bool _isWatchBusy;
    public bool HasWatchFolder => WatchFolder.Length > 0;
    public bool CanChooseWatchSecondLanguage => WatchLanguage != "auto";

    /// <summary>Whether the connected server tells speakers apart; the choice is shown only then, and an older server is sent none.</summary>
    public bool CanTellSpeakersApart => _connection()?.Info.Speakers == true;

    /// <summary>The second language of watched recordings, or empty. (A list that is being replaced sets it to null for a moment: that is not a choice.)</summary>
    public string? WatchSecondLanguage
    {
        get => _secondLanguage;
        set
        {
            if (value is null || value == _secondLanguage) return;
            _secondLanguage = value;
            OnPropertyChanged();
            Save();
        }
    }

    /// <summary>What watched recordings are sent to be read in: <c>auto</c>, <c>en</c> or <c>en+hu</c> (an older server is sent the first language only).</summary>
    private string LanguageChoice => SpeechLanguages.Join(WatchLanguage, _connection()?.Info.LanguagePairs == true ? _secondLanguage : "");

    private void Load()
    {
        _loading = true;
        try
        {
            var choices = File.Exists(_choicesPath) ? JsonSerializer.Deserialize<Choices>(File.ReadAllText(_choicesPath)) ?? new() : new Choices();
            WatchFolder = choices.Folder; WatchEnabled = choices.Enabled && choices.Folder.Length > 0;
            (WatchLanguage, WatchSecondLanguage) = SpeechLanguages.Split(choices.Language);
            WatchOutput = WatchOutputs.Contains(choices.Output) ? choices.Output : ShellViewModel.WatchAsText;
            WatchSpeakers = choices.Speakers;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }   // the defaults: nothing is watched
        finally { _loading = false; }
    }

    private void Save()
    {
        if (_loading) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_choicesPath)!);
            File.WriteAllText(_choicesPath, JsonSerializer.Serialize(new Choices(WatchFolder, WatchEnabled, SpeechLanguages.Join(WatchLanguage, _secondLanguage), WatchOutput, WatchSpeakers)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _reportError(Loc.T("Preferences could not be saved"), Loc.Describe(error.Message)); }
    }

    public void SetWatchFolder(string path)
    {
        _loading = true;
        WatchFolder = Path.GetFullPath(path); WatchEnabled = true;
        _loading = false;
        Save();
        _ = RestartAsync();
    }

    partial void OnWatchFolderChanged(string value) { OnPropertyChanged(nameof(HasWatchFolder)); if (!_loading) { Save(); _ = RestartAsync(); } }
    partial void OnWatchEnabledChanged(bool value) { if (!_loading) { Save(); _ = RestartAsync(); } }
    partial void OnWatchOutputChanged(string value) => Save();
    partial void OnWatchSpeakersChanged(string value) => Save();
    partial void OnWatchLanguageChanged(string value)
    {
        if (value == "auto" || value == _secondLanguage) { _secondLanguage = ""; OnPropertyChanged(nameof(WatchSecondLanguage)); }
        _secondLanguages = null; OnPropertyChanged(nameof(WatchSecondLanguages));
        Save();
    }

    /// <summary>The connection changed: what the server can do decides some of the choices, and recordings that waited for a server can go now.</summary>
    public void ServerChanged()
    {
        OnPropertyChanged(nameof(CanTellSpeakersApart));
        _onUi(RefreshStatus);
    }

    public void RefreshTexts()
    {
        _languages = null; OnPropertyChanged(nameof(Languages));
        _secondLanguages = null; OnPropertyChanged(nameof(WatchSecondLanguages));
        RefreshStatus();
    }

    [RelayCommand]
    private void OpenWatchFolder()
    {
        if (!Directory.Exists(WatchFolder)) { _reportError(Loc.T("Folder not found"), Loc.T("The watched folder does not exist any more. Choose it again.")); return; }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{WatchFolder}\"") { UseShellExecute = true });
    }

    /// <summary>(Re)starts the watcher to match the choices. Only recordings added after watching is switched on are sent (see <see cref="WatchLedger"/>).</summary>
    public async Task RestartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_watcher is { } old) { _watcher = null; await old.DisposeAsync(); }
            if (!WatchEnabled) { _onUi(() => WatchStatus = Loc.T("Off")); return; }
            if (WatchFolder.Length == 0 || !Directory.Exists(WatchFolder)) { _onUi(() => WatchStatus = Loc.T("The folder was not found. Choose it again.")); return; }
            var ledger = await WatchLedger.LoadAsync(_ledgerPath);
            await ledger.EnsureBaselineAsync(WatchFolder);
            var watcher = new FolderWatcher(WatchFolder, ledger, ProcessAsync, _options);
            watcher.Changed += () => _onUi(RefreshStatus);
            watcher.Start();
            _watcher = watcher;
            _onUi(RefreshStatus);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            _onUi(() => WatchStatus = Loc.T("Cannot watch this folder: {0}", Loc.Describe(error.Message)));
        }
        finally { _gate.Release(); }
    }

    private void RefreshStatus()
    {
        if (_watcher is not { } watcher) return;
        var waiting = watcher.Waiting;
        WatchStatus = IsWatchBusy
            ? Loc.T("Transcribing {0}", _current) + (waiting == 2 ? " · " + Loc.T("1 more recording waiting") : waiting > 2 ? " · " + Loc.T("{0} more recordings waiting", waiting - 1) : "")
            : _connection() is null ? Loc.T("Watching {0} · new recordings wait until this computer is connected to a server", WatchFolder)
            : waiting == 0 ? Loc.T("Watching {0}", WatchFolder)
            : waiting == 1 ? Loc.T("Watching {0} · 1 recording arriving", WatchFolder)
            : Loc.T("Watching {0} · {1} recordings arriving", WatchFolder, waiting);
    }

    /// <summary>Sends one recording, waits for the server to transcribe it, and saves the transcript next to it.</summary>
    private async Task<WatchOutcome> ProcessAsync(string path, CancellationToken token)
    {
        var name = Path.GetFileName(path);
        if (_connection() is not { } connection) { _onUi(RefreshStatus); return WatchOutcome.Retry; }      // picked up again at the next look at the folder
        var language = LanguageChoice;
        var speakers = connection.Info.Speakers ? WatchSpeakers : "off";
        _onUi(() => { _current = name; IsWatchBusy = true; RefreshStatus(); });
        try
        {
            var job = await connection.Client.UploadAsync(path, language, null, token, speakers);
            _onUi(() => _sent(job));
            while (!job.IsFinished) job = await connection.Client.GetAsync(job.Id, token, waitSeconds: 30);
            if (job.State != "complete")
            {
                _onUi(() => _reportError(Loc.T("Watched recording failed"), Loc.T("{0}: {1}", name, job.Error is { } error ? Loc.Describe(error) : Loc.T("Cancelled"))));
                return WatchOutcome.Failed;
            }
            try { await SaveNextToAsync(connection, job.Id, path, token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or RemoteException)
            { _onUi(() => _reportError(Loc.T("Could not save the transcript next to the recording"), Loc.T("{0}: {1}\nThe transcript is still in Projects.", name, Loc.Describe(error.Message)))); }
            return WatchOutcome.Done;
        }
        catch (OperationCanceledException) { return WatchOutcome.Retry; }
        catch (RemoteException error) when (error.Code == "models_missing")
        {
            _onUi(() =>
            {
                WatchEnabled = false;
                WatchStatus = Loc.T("Paused: the server is missing a speech model. Download it on the server, then switch watching back on.");
                _reportError(Loc.T("Watch folder paused"), Loc.T("{0} arrived, but the server cannot transcribe this language yet: its speech models are not downloaded.\nThe recording stays waiting and is sent when watching is switched on again.", name));
            });
            return WatchOutcome.Retry;
        }
        catch (RemoteException error) when (error.IsUnreachable || error.IsAuthentication || error.IsIdentityChanged || error.Code == "queue_full")
        {
            return WatchOutcome.Retry;     // the server is gone or busy for now: the recording is sent again later
        }
        catch (RemoteException error)
        {
            _onUi(() => _reportError(Loc.T("Watched recording failed"), Loc.T("{0}: {1}", name, Loc.Describe(error.Message))));
            return WatchOutcome.Failed;
        }
        finally { _onUi(() => { IsWatchBusy = false; RefreshStatus(); }); }
    }

    /// <summary>Writes the server's transcript beside the recording in the chosen format, never over an existing file; nothing when the choice is "Project only".</summary>
    private async Task SaveNextToAsync(RemoteConnection connection, Guid id, string recording, CancellationToken token)
    {
        if (WatchOutput == ShellViewModel.WatchProjectOnly) return;
        var mode = _exportMode() == "Readable" ? "readable" : "strict";
        byte[] content; var extension = ".txt";
        if (WatchOutput == ShellViewModel.WatchAsSubtitles)
        {
            // Subtitles need the timestamps of the programs; a transcript without them is saved as text instead (as Studio does).
            try { content = await connection.Client.ExportAsync(id, "srt", mode, token); extension = ".srt"; }
            catch (RemoteException error) when (error.Code == "subtitles_unavailable") { content = await connection.Client.ExportAsync(id, "txt", mode, token); }
        }
        else content = await connection.Client.ExportAsync(id, "txt", mode, token);
        var target = WatchRules.UniqueSibling(Path.ChangeExtension(recording, extension));
        await File.WriteAllBytesAsync(target, content, token);
    }

    /// <summary>Stops watching without waiting; the recording being sent is left for the next start.</summary>
    public void StopForExit() { if (_watcher is { } watcher) { _watcher = null; _ = watcher.DisposeAsync().AsTask(); } }

    public async ValueTask DisposeAsync()
    {
        if (_watcher is { } watcher) { _watcher = null; await watcher.DisposeAsync(); }
    }
}
