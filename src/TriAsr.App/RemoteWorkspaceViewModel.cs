using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TriAsr.Domain;
using TriAsr.Export;
using TriAsr.Infrastructure;

namespace TriAsr.App;

/// <summary>One recording on the server, as the project list shows it.</summary>
public sealed partial class RemoteJobRow(RemoteJob job) : ObservableObject
{
    public Guid Id { get; } = job.Id;
    public RemoteJob Job { get; private set; } = job;
    public string Name => Job.Name;
    public int Percent => Job.Percent;
    public bool IsRunning => Job.State == "running";
    public bool IsFinished => Job.IsFinished;
    public bool CanOpen => Job.State == "complete";
    public bool CanCancel => !Job.IsFinished;
    /// <summary>A recording can be deleted once it is finished, has failed or was cancelled.</summary>
    public bool CanDelete => Job.IsFinished;
    public string Created => Job.CreatedUtc.ToLocalTime().ToString("g");
    public string ErrorText => Job.Error is { } error ? Loc.Describe(error) : "";
    public bool HasError => Job.Error is not null;

    public string StateText => Job.State switch
    {
        "queued" => Loc.T("Queued"), "complete" => Loc.T("Complete"), "failed" => Loc.T("Failed"), "cancelled" => Loc.T("Cancelled"),
        _ => Job.Stage is { Length: > 0 } stage ? Loc.T(stage) : Loc.T("Preparing transcription")
    };

    public void Update(RemoteJob job)
    {
        Job = job;
        foreach (var property in new[] { nameof(Name), nameof(Percent), nameof(IsRunning), nameof(IsFinished), nameof(CanOpen), nameof(CanCancel), nameof(CanDelete), nameof(StateText), nameof(ErrorText), nameof(HasError), nameof(Created) })
            OnPropertyChanged(property);
    }

    public void RefreshTexts() { OnPropertyChanged(nameof(StateText)); OnPropertyChanged(nameof(ErrorText)); OnPropertyChanged(nameof(Created)); }
}

/// <summary>
/// The pages of a connected server: send a recording, follow the recordings on the server, review and edit a transcript with its audio, and export
/// it. Nothing is transcribed here; the recording goes to the server, which runs the speech programs, and the transcript comes back.
/// </summary>
public sealed partial class RemoteWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly Action<Action> _onUi;
    private readonly Action<string, string> _reportError;
    private readonly string _tempRoot;
    private RemoteConnection? _connection;
    private readonly string _recordingsFolder;
    private readonly string _dataRoot;
    private DictationViewModel? _dictation;

    /// <summary>
    /// Live dictation through this server: the phrases are recorded and typed here, and the server reads them (ADR: live dictation). The window with the keys and the little window
    /// are the same as in Studio.
    /// </summary>
    public DictationViewModel Dictation => _dictation ??= new DictationViewModel(Microphone, ServerEngine, DictationOutput, new DictationSettingsStore(_dataRoot), _onUi);

    /// <summary>Where the words go. Only a test changes it, and before dictation is first used.</summary>
    public ITextOutput DictationOutput { get; set; } = new WindowsKeyboard();

    private NotesViewModel? _notes;

    /// <summary>
    /// The notes of the connected server (ADR: notes), written and recorded here and kept there, so that the Client, the web page and Studio's own window see the same ones.
    /// In the Client the keys of recording work in every program through this page; in Studio they belong to Studio's own notes.
    /// </summary>
    public NotesViewModel Notes => _notes ??= MakeNotes();

    private NotesViewModel MakeNotes()
    {
        var notes = new NotesViewModel(() => _connection is { Info.NotesEnabled: true } ? new RemoteNoteSource(() => _connection?.Client) : null, Microphone, ServerEngine,
            new NotesSettingsStore(_dataRoot), _onUi, ownsHotkey: Edition.IsClient,
            noSourceText: () => _connection is null ? Loc.T("Connect to a server to use its notes.") : Loc.T("This server does not keep notes. It may be an older version."));
        notes.ConnectionLost += reason => ConnectionLost?.Invoke(reason);
        return notes;
    }

    private DictationEngine ServerEngine()
    {
        if (_connection is not { } connection) return new(null, Loc.T("Connect to a server to dictate."));
        if (!connection.Info.LiveEnabled) return new(null, Loc.T("This server cannot read dictation. It may be an older version, or have no speech model downloaded yet."));
        return new(new RemoteLiveRecognizer(connection.Client));
    }
    private RecorderViewModel? _recorder;

    /// <summary>The link card: the server fetches the sound of a web address and transcribes it.</summary>
    public RemoteLinkViewModel Link { get; }

    /// <summary>What the connected server said about itself last, or null when there is no connection.</summary>
    public RemoteServerInfo? ServerInfo => _connection?.Info;

    /// <summary>Shows a recording the server took (a link it is fetching) in the project list.</summary>
    internal void ShowSent(RemoteJob job)
    {
        Interlocked.Increment(ref _localChanges);
        Merge([job]);
        SelectedJob = Jobs.FirstOrDefault(row => row.Id == job.Id);
        SelectedTab = "Projects";
    }

    /// <summary>The server stopped answering during a call that the link card made.</summary>
    internal void LoseConnection(string message) => ConnectionLost?.Invoke(message);

    /// <summary>The microphone, for sending a recording made here.</summary>
    public RecorderViewModel Recorder => _recorder ??= MakeRecorder();

    /// <summary>What recordings are made from. Only a test changes it, and before the recorder is first used.</summary>
    public TriAsr.Audio.Recording.IMicrophone Microphone { get; set; } = new TriAsr.Audio.Recording.WindowsMicrophone();

    private RecorderViewModel MakeRecorder()
    {
        var recorder = new RecorderViewModel(Microphone, _recordingsFolder, _onUi);
        recorder.Recorded += file => SourcePath = file.Path;
        return recorder;
    }

    private CancellationTokenSource? _polling, _audio;
    private Guid _reviewJob;
    private string _reviewLanguage = "";
    private string? _rawCanaryNote;

    /// <param name="dataRoot">The program's data folder, where the choices for live dictation are kept; without it they are kept in the temporary folder.</param>
    public RemoteWorkspaceViewModel(Action<Action> onUi, Action<string, string> reportError, string tempRoot, string? recordingsFolder = null, string? dataRoot = null)
    {
        _onUi = onUi; _reportError = reportError; _tempRoot = tempRoot; _dataRoot = dataRoot ?? tempRoot;
        Link = new RemoteLinkViewModel(this);
        _recordingsFolder = recordingsFolder ?? System.IO.Path.Combine(tempRoot, "Recordings");
        Loc.Instance.PropertyChanged += (_, change) => { if (change.PropertyName == nameof(Loc.Version)) _onUi(RefreshTexts); };
    }

    /// <summary>Raised when the server stops answering; the window then shows that the connection is gone.</summary>
    public event Action<string>? ConnectionLost;

    // ---- the connection ---------------------------------------------------------------------------------------------------------------

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _serverName = "";
    [ObservableProperty] private string _serverNote = "";
    [ObservableProperty] private bool _canSend;
    [ObservableProperty] private string _selectedTab = "New";

    public bool IsNewTab => SelectedTab == "New";
    public bool IsProjectsTab => SelectedTab == "Projects";
    public bool IsReviewTab => SelectedTab == "Review";
    public bool IsNotesTab => SelectedTab == "Notes";

    partial void OnSelectedTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsNewTab)); OnPropertyChanged(nameof(IsProjectsTab)); OnPropertyChanged(nameof(IsReviewTab)); OnPropertyChanged(nameof(IsNotesTab)); OnPropertyChanged(nameof(ShowPlayer));
        if (value == "Notes") _ = Notes.RefreshAsync();                       // another window may have saved notes meanwhile
    }

    [RelayCommand] private void ShowTab(string tab) => SelectedTab = tab;

    public RemoteServerClient? Client => _connection?.Client;
    public bool NotConnected => !IsConnected;
    public bool HasServerNote => ServerNote.Length > 0;
    public bool NoReview => Regions.Count == 0;
    public bool ShowPlayer => IsConnected && IsReviewTab && HasReview;
    public bool HasAudioStatus => AudioStatus.Length > 0;

    partial void OnIsConnectedChanged(bool value) { OnPropertyChanged(nameof(NotConnected)); OnPropertyChanged(nameof(ShowPlayer)); UpdateCanSend(); Link.ServerChanged(); }
    partial void OnServerNoteChanged(string value) => OnPropertyChanged(nameof(HasServerNote));
    partial void OnAudioStatusChanged(string value) => OnPropertyChanged(nameof(HasAudioStatus));

    /// <summary>Tells the user that the audio could not be played.</summary>
    public void ReportPlaybackError(string message) => _reportError(Loc.T("Audio playback failed"), message);

    /// <summary>Attaches a connection (or detaches with null) and starts following the recordings on the server.</summary>
    public void Attach(RemoteConnection? connection)
    {
        _polling?.Cancel(); _audio?.Cancel();
        _connection = connection;
        Jobs.Clear(); Regions.Clear(); SelectedJob = null; _reviewJob = Guid.Empty; ReviewSummary = ""; RawWhisper = ""; RawCanary = "";
        if (connection is null && _dictation is { IsListening: true } running) _ = running.StopAsync();      // the server is gone: nothing can read the phrases
        if (_notes is not null) _ = _notes.SourceChangedAsync();
        IsConnected = connection is not null;
        ServerName = connection?.Info.Name ?? "";
        SelectedTab = "New";
        RefreshServerNote(connection?.Info);
        if (connection is null) return;
        _polling = new CancellationTokenSource();
        _ = PollAsync(connection, _polling.Token);
    }

    private void RefreshServerNote(RemoteServerInfo? info)
    {
        ServerNote = info is { ModelsReady: false } ? Loc.T("This server cannot transcribe yet: its speech models are missing ({0}). Set it up on the server first.", string.Join(", ", info.MissingModels)) : "";
        UpdateCanSend();
        Link.ServerChanged();
        _dictation?.RefreshAvailability();
        _notes?.RefreshAvailability();
    }

    private void UpdateCanSend() => CanSend = IsConnected && !IsSending && File.Exists(SourcePath) && _connection?.Info.ModelsReady != false;

    private void RefreshTexts()
    {
        RefreshServerNote(_connection?.Info);
        _recorder?.RefreshTexts();
        Link.RefreshTexts();
        _dictation?.RefreshTexts();
        _notes?.RefreshTexts();
        foreach (var row in Jobs) row.RefreshTexts();
        foreach (var region in Regions) region.NotifyLanguageChanged();
        if (Regions.Count > 0) ShowReviewSummary();
        if (_rawCanaryNote is not null) RawCanary = CanaryNote(_rawCanaryNote);
        _languages = null; OnPropertyChanged(nameof(Languages));
        if (!IsSending && SendStatus.Length > 0 && !HasSendError) SendStatus = "";
    }

    // ---- sending a recording ------------------------------------------------------------------------------------------------------------

    private IReadOnlyList<LanguageOption>? _languages;
    private static readonly LanguageOption AutoDetect = new("auto", Loc.Key("Auto-detect language"));

    public IReadOnlyList<LanguageOption> Languages => _languages ??= LanguageText.InOrder(LanguageCatalog.All).Prepend(AutoDetect).ToArray();

    [ObservableProperty] private string _sourcePath = "";
    [ObservableProperty] private string _selectedLanguage = "auto";
    [ObservableProperty] private bool _isSending;
    [ObservableProperty] private double _sendPercent;
    [ObservableProperty] private string _sendStatus = "";
    [ObservableProperty] private bool _hasSendError;

    partial void OnSourcePathChanged(string value) => UpdateCanSend();
    partial void OnIsSendingChanged(bool value) => UpdateCanSend();

    /// <summary>Sends the chosen recording to the server. On success the recording appears in the project list.</summary>
    [RelayCommand]
    private async Task SendAsync()
    {
        if (_connection is not { } connection || !File.Exists(SourcePath) || IsSending) return;
        IsSending = true; HasSendError = false; SendPercent = 0; SendStatus = Loc.T("Sending to {0}…", ServerName);
        var length = Math.Max(1, new FileInfo(SourcePath).Length);
        try
        {
            var job = await connection.Client.UploadAsync(SourcePath, SelectedLanguage, new Progress<long>(bytes => SendPercent = Math.Min(100, bytes * 100d / length)), CancellationToken.None);
            Interlocked.Increment(ref _localChanges);
            Merge([job]);
            SelectedJob = Jobs.FirstOrDefault(row => row.Id == job.Id);
            SendStatus = Loc.T("Sent. The server is working on it.");
            SourcePath = "";
            SelectedTab = "Projects";
        }
        catch (RemoteException error) when (error.Code == "models_missing")
        {
            HasSendError = true; SendStatus = Loc.T("The server cannot transcribe this language yet: its speech models are not downloaded.");
        }
        catch (RemoteException error)
        {
            HasSendError = true; SendStatus = Loc.T("The recording could not be sent: {0}", Loc.Describe(error.Message));
            if (error.IsUnreachable) ConnectionLost?.Invoke(error.Message);
        }
        finally { IsSending = false; }
    }

    // ---- the recordings on the server -----------------------------------------------------------------------------------------------------

    public ObservableCollection<RemoteJobRow> Jobs { get; } = [];
    [ObservableProperty] private RemoteJobRow? _selectedJob;
    public bool HasNoJobs => Jobs.Count == 0;

    partial void OnSelectedJobChanged(RemoteJobRow? value) { OpenSelectedCommand.NotifyCanExecuteChanged(); CancelSelectedCommand.NotifyCanExecuteChanged(); }

    private void Merge(IReadOnlyList<RemoteJob> list, bool replace = false)
    {
        foreach (var job in list)
        {
            var row = Jobs.FirstOrDefault(item => item.Id == job.Id);
            if (row is null) Jobs.Insert(Jobs.TakeWhile(item => item.Job.CreatedUtc > job.CreatedUtc).Count(), new RemoteJobRow(job));
            else row.Update(job);
        }
        if (replace) foreach (var gone in Jobs.Where(row => list.All(job => job.Id != row.Id)).ToArray()) Jobs.Remove(gone);
        OnPropertyChanged(nameof(HasNoJobs));
        OpenSelectedCommand.NotifyCanExecuteChanged(); CancelSelectedCommand.NotifyCanExecuteChanged();
    }

    // Counts what this window itself added to or removed from the list. An answer that was asked for before such a change is out of date and must not undo it
    // (a refresh that started just before a recording was sent would otherwise take the new row away, or bring a deleted one back).
    private int _localChanges;

    private async Task PollAsync(RemoteConnection connection, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var asked = Volatile.Read(ref _localChanges);
                var list = await connection.Client.ListAsync(token);
                var info = await connection.Client.InfoAsync(token);
                _onUi(() =>
                {
                    if (!ReferenceEquals(_connection?.Client, connection.Client)) return;
                    if (asked == _localChanges) Merge(list, replace: true);
                    if (!ReferenceEquals(_connection.Info, info)) { _connection = _connection with { Info = info }; RefreshServerNote(info); }
                    if (SelectedTab == "Notes" && _notes is { IsListening: false }) _ = _notes.RefreshAsync();         // the notes of the server change when others write
                });
                await Task.Delay(list.Any(job => !job.IsFinished) ? TimeSpan.FromMilliseconds(1200) : TimeSpan.FromSeconds(6), token);
            }
            catch (OperationCanceledException) { return; }
            catch (RemoteException error) when (error.IsUnreachable || error.IsAuthentication || error.IsIdentityChanged || error.Status >= 500)
            {
                _onUi(() => { if (ReferenceEquals(_connection?.Client, connection.Client)) ConnectionLost?.Invoke(error.Message); });
                return;
            }
            catch (RemoteException) { await Task.Delay(TimeSpan.FromSeconds(6), token); }
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenSelected))]
    private async Task OpenSelectedAsync()
    {
        if (SelectedJob is { } row) await OpenReviewAsync(row.Id);
    }

    private bool CanOpenSelected() => SelectedJob?.CanOpen == true;

    [RelayCommand(CanExecute = nameof(CanCancelSelected))]
    private async Task CancelSelectedAsync()
    {
        if (SelectedJob is not { } row || _connection is not { } connection) return;
        try { Merge([await connection.Client.CancelAsync(row.Id, CancellationToken.None)]); }
        catch (RemoteException error) { _reportError(Loc.T("Could not cancel the recording"), Loc.Describe(error.Message)); }
    }

    private bool CanCancelSelected() => SelectedJob?.CanCancel == true;

    /// <summary>A click on a recording of the list: a finished one opens in Review, any other is only selected (so that Cancel applies to it).</summary>
    [RelayCommand]
    private async Task OpenJobAsync(RemoteJobRow? row)
    {
        if (row is null) return;
        SelectedJob = row;
        if (row.CanOpen) await OpenReviewAsync(row.Id);
    }

    /// <summary>Deletes a finished recording on the server (the window has asked first), and lets go of its review if that is open.</summary>
    [RelayCommand]
    private async Task DeleteJobAsync(RemoteJobRow? row)
    {
        row ??= SelectedJob;
        if (row is null || _connection is not { } connection) return;
        if (!row.CanDelete) { _reportError(Loc.T("Could not delete the project"), Loc.T("A project that is still being worked on cannot be deleted. Cancel it first.")); return; }
        try
        {
            await connection.Client.DeleteAsync(row.Id, CancellationToken.None);
            Interlocked.Increment(ref _localChanges);
            if (_reviewJob == row.Id) ClearReview();
            Jobs.Remove(row);
            if (SelectedJob == row) SelectedJob = null;
            OnPropertyChanged(nameof(HasNoJobs));
            ForgetDownloadedAudio(row.Id);
        }
        catch (RemoteException error) when (error.Code == "still_running")
        {
            _reportError(Loc.T("Could not delete the project"), Loc.T("A project that is still being worked on cannot be deleted. Cancel it first."));
        }
        catch (RemoteException error)
        {
            _reportError(Loc.T("Could not delete the project"), Loc.Describe(error.Message));
            if (error.IsUnreachable) ConnectionLost?.Invoke(error.Message);
        }
    }

    /// <summary>The open review is gone (its recording was deleted): the page goes back to the list.</summary>
    private void ClearReview()
    {
        _audio?.Cancel();
        Regions.Clear(); SelectedRegion = null;
        _reviewJob = Guid.Empty; _rawCanaryNote = null;
        ReviewName = ""; ReviewSummary = ""; RawWhisper = ""; RawCanary = ""; AudioStatus = "";
        AudioSource = null; NormalizedAudioPath = "";
        OnPropertyChanged(nameof(HasReview)); OnPropertyChanged(nameof(NoReview)); OnPropertyChanged(nameof(ShowPlayer));
        if (SelectedTab == "Review") SelectedTab = "Projects";
    }

    /// <summary>The audio that was fetched for the review of a deleted recording is removed from this computer too, as soon as the player lets go of it.</summary>
    private void ForgetDownloadedAudio(Guid id)
    {
        var folder = Path.Combine(_tempRoot, id.ToString("N"));
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 5 && Directory.Exists(folder); attempt++)
            {
                try { Directory.Delete(folder, true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { await Task.Delay(300); }
            }
        });
    }

    // ---- reviewing ---------------------------------------------------------------------------------------------------------------------

    public ObservableCollection<ReviewRegion> Regions { get; } = [];
    public ICollectionView ReviewItems { get; private set; } = null!;
    [ObservableProperty] private ReviewRegion? _selectedRegion;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _uncertainOnly;
    [ObservableProperty] private string _reviewSummary = "";
    [ObservableProperty] private string _rawWhisper = "";
    [ObservableProperty] private string _rawCanary = "";
    [ObservableProperty] private string _reviewName = "";
    [ObservableProperty] private Uri? _audioSource;
    [ObservableProperty] private string _normalizedAudioPath = "";
    [ObservableProperty] private string _audioStatus = "";
    [ObservableProperty] private string _selectedExportMode = "Readable";

    public IReadOnlyList<string> ExportModes { get; } = [Loc.Key("Readable"), Loc.Key("Strict Verbatim")];

    public bool HasReview => Regions.Count > 0;

    public void InitializeReviewView()
    {
        ReviewItems = CollectionViewSource.GetDefaultView(Regions);
        ReviewItems.Filter = item => item is ReviewRegion region && (!UncertainOnly || region.IsUncertain)
            && (string.IsNullOrWhiteSpace(SearchText) || region.Text.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            || region.Whisper.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) || region.Canary.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase));
        OnPropertyChanged(nameof(ReviewItems));
    }

    partial void OnSearchTextChanged(string value) => ReviewItems?.Refresh();
    partial void OnUncertainOnlyChanged(bool value) => ReviewItems?.Refresh();

    private static string CanaryNote(string code) => code == "outside-coverage"
        ? Loc.T("This language is outside Canary's coverage. Whisper timestamps and text are preserved; all regions require listening.")
        : Loc.T("Canary did not complete. All regions require listening.");

    private void ShowReviewSummary() =>
        ReviewSummary = Loc.T("{0} · regions: {1} · to listen to: {2}", _reviewLanguage.ToUpperInvariant(), Regions.Count, Regions.Count(region => region.IsUncertain));

    /// <summary>Fetches a finished transcript with the programs' own texts and shows it for review; the audio comes across in the background.</summary>
    public async Task OpenReviewAsync(Guid id)
    {
        if (_connection is not { } connection) return;
        try
        {
            var review = await connection.Client.ReviewAsync(id, CancellationToken.None);
            InitializeReviewViewIfNeeded();
            Regions.Clear();
            for (var i = 0; i < review.Regions.Count; i++) Regions.Add(new ReviewRegion(review.Regions[i], i < review.AutomaticTexts.Count ? review.AutomaticTexts[i] : null));
            _reviewJob = id; _reviewLanguage = review.Language;
            ReviewName = Jobs.FirstOrDefault(row => row.Id == id)?.Name ?? "";
            RawWhisper = review.RawWhisper; _rawCanaryNote = review.RawCanaryNote;
            RawCanary = review.RawCanaryNote is { } note ? CanaryNote(note) : review.RawCanary;
            SelectedRegion = Regions.FirstOrDefault();
            ShowReviewSummary();
            OnPropertyChanged(nameof(HasReview)); OnPropertyChanged(nameof(NoReview)); OnPropertyChanged(nameof(ShowPlayer));
            SelectedTab = "Review";
            _ = DownloadAudioAsync(connection, id);
        }
        catch (RemoteException error) { _reportError(Loc.T("Cannot open transcript"), Loc.Describe(error.Message)); if (error.IsUnreachable) ConnectionLost?.Invoke(error.Message); }
    }

    private void InitializeReviewViewIfNeeded() { if (ReviewItems is null) InitializeReviewView(); }

    private async Task DownloadAudioAsync(RemoteConnection connection, Guid id)
    {
        _audio?.Cancel();
        var cancellation = _audio = new CancellationTokenSource();
        AudioSource = null; NormalizedAudioPath = "";
        AudioStatus = Loc.T("Getting the audio from the server…");
        var folder = Path.Combine(_tempRoot, id.ToString("N"));
        try
        {
            var normalized = Path.Combine(folder, "normalized.wav");
            var playback = Path.Combine(folder, "playback.m4a");
            if (!File.Exists(normalized)) await connection.Client.DownloadAudioAsync(id, "normalized", normalized, null, cancellation.Token);
            if (cancellation.IsCancellationRequested || _reviewJob != id) return;
            NormalizedAudioPath = normalized;
            try { if (!File.Exists(playback)) await connection.Client.DownloadAudioAsync(id, "playback", playback, null, cancellation.Token); }
            catch (RemoteException) { playback = normalized; }   // no listening copy: the 16 kHz file plays too
            if (cancellation.IsCancellationRequested || _reviewJob != id) return;
            AudioSource = new Uri(File.Exists(playback) ? playback : normalized);
            AudioStatus = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is RemoteException or IOException or UnauthorizedAccessException)
        {
            AudioStatus = Loc.T("The audio could not be fetched: {0}", error is RemoteException ? Loc.Describe(error.Message) : error.Message);
        }
    }

    [RelayCommand] private void UseWhisper() { if (SelectedRegion is not null) SelectedRegion.Text = SelectedRegion.Whisper; }
    [RelayCommand] private void UseCanary() { if (SelectedRegion is not null) SelectedRegion.Text = SelectedRegion.Canary; }
    [RelayCommand] private void UseAutomatic() { if (SelectedRegion is not null) SelectedRegion.Text = SelectedRegion.MachineText; }

    public void MoveReview(int direction)
    {
        var visible = ReviewItems.Cast<ReviewRegion>().Where(region => region.IsUncertain || region.Source == "llm-arbitrated").ToArray();
        if (visible.Length == 0) return;
        var index = Array.IndexOf(visible, SelectedRegion);
        SelectedRegion = visible[Math.Clamp(index + direction, 0, visible.Length - 1)];
    }

    /// <summary>The transcript as it stands, with the edits made so far.</summary>
    public FinalTranscript? CurrentTranscript => Regions.Count == 0 ? null : new FinalTranscript(_reviewJob, _reviewLanguage, Regions.Select(region => region.Snapshot()).ToArray());

    /// <summary>Sends the changed texts to the server, which keeps them with the project.</summary>
    [RelayCommand]
    private async Task SaveReviewAsync()
    {
        if (_connection is not { } connection || Regions.Count == 0) return;
        var edits = Regions.Select((region, index) => (region, index)).Where(item => item.region.Text != item.region.Original.FinalText).Select(item => new RemoteEdit(item.index, item.region.Text)).ToArray();
        if (edits.Length == 0) return;
        try
        {
            await connection.Client.SaveEditsAsync(_reviewJob, edits, CancellationToken.None);
            foreach (var region in Regions) region.AcceptSaved();
            ReviewItems.Refresh();
            ReviewSaved?.Invoke();
        }
        catch (RemoteException error) { _reportError(Loc.T("Save failed"), Loc.Describe(error.Message)); if (error.IsUnreachable) ConnectionLost?.Invoke(error.Message); }
    }

    /// <summary>Raised after the edits reached the server (the window says so in its status line).</summary>
    public event Action? ReviewSaved;

    /// <summary>Writes the transcript to a file in the format the file name asks for, from what is on screen. The server is not involved.</summary>
    public async Task ExportAsync(string path)
    {
        if (CurrentTranscript is not { } transcript) { _reportError(Loc.T("No transcript open"), Loc.T("Open a transcript before exporting.")); return; }
        try
        {
            var exported = SelectedExportMode == "Readable" ? TranscriptExporter.ReadableCopy(transcript) : transcript;
            await TranscriptExporter.SaveAsync(exported, path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { _reportError(Loc.T("Export failed"), Loc.Describe(error.Message)); }
    }

    public void Dispose()
    {
        _polling?.Cancel(); _audio?.Cancel();
        _polling?.Dispose(); _audio?.Dispose();
        _recorder?.Dispose();   // a recording that is running is saved
        _dictation?.Dispose();
        _notes?.Dispose();
    }
}
