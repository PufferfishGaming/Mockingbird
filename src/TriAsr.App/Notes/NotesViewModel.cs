using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TriAsr.Application;
using TriAsr.Audio.Recording;
using TriAsr.Domain;

namespace TriAsr.App;

/// <summary>One note in the list.</summary>
public sealed partial class NoteRow(NoteInfo info) : ObservableObject
{
    public Guid Id { get; } = info.Id;
    public NoteInfo Info { get; private set; } = info;
    public int Revision => Info.Revision;
    public DateTimeOffset UpdatedUtc => Info.UpdatedUtc;

    /// <summary>The title, or the first words of the note when it has none, or that it is untitled.</summary>
    public string DisplayTitle => Info.Title.Length > 0 ? Info.Title : Info.Preview.Length > 0 ? Shorten(Info.Preview, 48) : Loc.T("Untitled note");

    /// <summary>The first words under the title; a note without a title already shows them as its title.</summary>
    public string PreviewLine => Info.Title.Length > 0 ? Info.Preview : "";
    public bool HasPreview => PreviewLine.Length > 0;
    public string Updated => Info.UpdatedUtc.ToLocalTime().ToString("g");

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..length].TrimEnd() + "…";

    public void Update(NoteInfo info)
    {
        Info = info;
        foreach (var property in new[] { nameof(DisplayTitle), nameof(PreviewLine), nameof(HasPreview), nameof(Updated), nameof(Revision), nameof(UpdatedUtc) }) OnPropertyChanged(property);
    }

    public void RefreshTexts() { OnPropertyChanged(nameof(DisplayTitle)); OnPropertyChanged(nameof(Updated)); }
}

/// <summary>
/// The notes page (ADR: notes): a list of notes and an editor, where a note can also be dictated. The note that is open saves itself a moment after the last change;
/// the notes are this computer's own (Studio) or a server's (the Client, the Remote server page). Recording a note is a <see cref="LiveSession"/>: every phrase that
/// is spoken is read and added to the end of the open note. The keys of recording work in every program and show the little window while they record.
/// </summary>
public sealed partial class NotesViewModel : ObservableObject, IDisposable, ILiveOverlay
{
    /// <summary>The name of the notes among the hotkeys of the program.</summary>
    public const string HotkeyAction = "notes";

    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(900), RetryDelay = TimeSpan.FromSeconds(5);

    private readonly Func<INoteSource?> _source;
    private readonly IMicrophone _microphone;
    private readonly Func<DictationEngine> _engine;
    private readonly NotesSettingsStore _store;
    private readonly Action<Action> _onUi;
    private readonly bool _ownsHotkey;
    private readonly Func<string>? _noSourceText;
    private readonly LiveSession _session;
    private readonly SemaphoreSlim _saving = new(1, 1);
    private Func<string>? _statusMake;
    private NotesSettings _saved;
    private bool _restoring, _loading, _dirty, _failed, _isSaving, _autoOverlay;
    private Func<string>? _failureText;
    private bool _silentSelection;
    private Guid? _openId;
    private int _revision;
    private int _openVersion;
    private Task _opening = Task.CompletedTask;
    private IReadOnlyList<LanguageOption>? _languages, _secondLanguages;
    private IHotkeys? _hotkeys;
    private IDisposable? _paused;
    private System.Windows.Threading.DispatcherTimer? _saveTimer, _levelTimer;

    /// <param name="source">The notes to show now, or null when there are none to be had (no server is connected).</param>
    /// <param name="noSourceText">What to say when there is no source (no server is connected, or it keeps no notes).</param>
    /// <param name="ownsHotkey">Whether the keys of recording work in every program through this page. Studio's page of the server's notes does not: the keys belong to Studio's own notes.</param>
    public NotesViewModel(Func<INoteSource?> source, IMicrophone microphone, Func<DictationEngine> engine, NotesSettingsStore store, Action<Action> onUi, bool ownsHotkey = true, Func<string>? noSourceText = null)
    {
        _source = source; _microphone = microphone; _engine = engine; _store = store; _onUi = onUi; _ownsHotkey = ownsHotkey; _noSourceText = noSourceText;
        _saved = store.Load();
        _session = new LiveSession(microphone, onUi);
        _session.Reading += () => SetStatus(() => Loc.T("Reading what you said…"));
        _session.Settled += () => SetStatus(() => Loc.T("Listening…"));
        _session.Problem += make => SetStatus(make);
        _session.MicrophoneFailed += make =>
        {
            if (!IsListening) return;
            _ = StopRecordingAsync().ContinueWith(_ => _onUi(() => SetStatus(make)), TaskScheduler.Default);
        };
        Keybind = new KeybindViewModel(KeyCombo.ParseOr(_saved.Hotkey, KeyCombo.None), KeyCombo.None, RefuseKeys, Capturing, OnKeysChosen);
        _restoring = true;
        (SelectedLanguage, SelectedSecondLanguage) = SpeechLanguages.Split(_saved.Language);
        RefreshDevices();
        _restoring = false;
        RefreshAvailability();
        SetStatus(null);
    }

    /// <summary>Set by the window: shows the little window while a note is recorded with the keys.</summary>
    public IOverlayPresenter? Presenter { get; set; }

    /// <summary>Set by the window: the keys that work in every program.</summary>
    public IHotkeys? Hotkeys
    {
        get => _hotkeys;
        set
        {
            _hotkeys = value;
            if (!_ownsHotkey) return;
            value?.Claim(HotkeyAction, Keybind.Current);
            RegisterHotkey();
        }
    }

    /// <summary>The keys that start and stop recording a note, and choosing others by pressing them.</summary>
    public KeybindViewModel Keybind { get; }

    /// <summary>Whether the keys can be chosen here (they belong to the notes of this computer, not to the server's).</summary>
    public bool ShowsKeybind => _ownsHotkey;

    public ObservableCollection<NoteRow> Notes { get; } = [];
    public ObservableCollection<DeviceChoice> Devices { get; } = [];
    public IReadOnlyList<LanguageOption> Languages => _languages ??= SpeechLanguages.First();
    public IReadOnlyList<LanguageOption> SecondLanguages => _secondLanguages ??= SpeechLanguages.Second(SelectedLanguage);
    private string _selectedSecondLanguage = "";

    /// <summary>The second language of a person who switches between two, or empty. (A list that is being replaced sets it to null for a moment: that is not a choice.)</summary>
    public string? SelectedSecondLanguage
    {
        get => _selectedSecondLanguage;
        set
        {
            if (value is null || value == _selectedSecondLanguage) return;
            _selectedSecondLanguage = value;
            OnPropertyChanged();
            Change(settings => settings with { Language = LanguageChoice });
        }
    }

    /// <summary>A second language can be chosen with a first one; auto-detect does without.</summary>
    public bool CanChooseSecondLanguage => SelectedLanguage != "auto";

    /// <summary>What the phrases are read in: <c>auto</c>, <c>en</c> or <c>en+hu</c>.</summary>
    public string LanguageChoice => SpeechLanguages.Join(SelectedLanguage, _selectedSecondLanguage);

    [ObservableProperty] private NoteRow? _selectedNote;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _text = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanChooseSecondLanguage))] private string _selectedLanguage = "auto";
    [ObservableProperty] private DeviceChoice? _selectedDevice;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSwitch)), NotifyCanExecuteChangedFor(nameof(NewNoteCommand))] private bool _isListening;
    [ObservableProperty] private bool _isOverlayVisible;
    [ObservableProperty] private double _level;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _lastText = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _hotkeyNote = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNotice))] private string _notice = "";

    public bool HasOpenNote => _openId is not null;
    public bool NoNoteOpen => _openId is null;
    public bool HasSource => _source() is not null;
    public bool NoSource => !HasSource;

    /// <summary>Why there are no notes to show: no server is connected, or it keeps none.</summary>
    public string NoSourceText => _noSourceText?.Invoke() ?? Loc.T("Connect to a server to use its notes.");
    public bool HasNotes => Notes.Count > 0;
    public bool HasNote => Note.Length > 0;
    public bool HasHotkeyNote => HotkeyNote.Length > 0;
    public bool HasNotice => Notice.Length > 0;

    /// <summary>The list and the New button wait while a note is being recorded: the words go to the note that is open.</summary>
    public bool CanSwitch => !IsListening;
    public bool IsAvailable => _engine().Recognizer is not null;
    public string RecordButtonLabel => IsListening ? Loc.T("Stop recording") : Loc.T("Record");

    ICommand ILiveOverlay.ToggleListeningCommand => ToggleRecordingCommand;

    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));
    partial void OnHotkeyNoteChanged(string value) => OnPropertyChanged(nameof(HasHotkeyNote));
    partial void OnSelectedLanguageChanged(string value)
    {
        if (value == "auto" || value == _selectedSecondLanguage) { _selectedSecondLanguage = ""; OnPropertyChanged(nameof(SelectedSecondLanguage)); }
        _secondLanguages = null; OnPropertyChanged(nameof(SecondLanguages));
        Change(settings => settings with { Language = LanguageChoice });
    }
    partial void OnSelectedDeviceChanged(DeviceChoice? value) => Change(settings => settings with { Microphone = value?.Id ?? WindowsMicrophone.DefaultDevice });
    partial void OnIsListeningChanged(bool value) => OnPropertyChanged(nameof(RecordButtonLabel));
    partial void OnSelectedNoteChanged(NoteRow? value) { if (!_silentSelection) _opening = OpenAsync(value); }
    partial void OnTitleChanged(string value) { if (!_loading) Edited(); }
    partial void OnTextChanged(string value) { if (!_loading) Edited(); }

    /// <summary>Finishes whatever opening or loading a note is going on (a test waits for it).</summary>
    public Task Settled => _opening;

    /// <summary>Changes one thing the person chose. The file is read again first: Studio's notes and the notes of its Remote server page keep their choices in the same file, and the other may have changed something else meanwhile.</summary>
    private void Change(Func<NotesSettings, NotesSettings> change)
    {
        if (_restoring) return;
        _saved = change(_store.Load());
        _store.Save(_saved);
    }

    public void RememberPosition(double left, double top) => Change(settings => settings with { Left = left, Top = top });

    public (double Left, double Top)? Position => _saved is { Left: { } left, Top: { } top } ? (left, top) : null;

    public void RefreshDevices()
    {
        var keep = SelectedDevice?.Id ?? _saved.Microphone;
        Devices.Clear();
        foreach (var device in _microphone.Devices()) Devices.Add(new DeviceChoice(device));
        SelectedDevice = Devices.FirstOrDefault(choice => choice.Id == keep) ?? Devices.FirstOrDefault();
    }

    /// <summary>Looks again at whether notes can be recorded (a model was downloaded, a server was connected) and says why not.</summary>
    public void RefreshAvailability()
    {
        Note = _engine().Note;
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(HasSource)); OnPropertyChanged(nameof(NoSource)); OnPropertyChanged(nameof(NoSourceText));
        if (!IsListening) SetStatus(null);
    }

    // ---- the list ---------------------------------------------------------------------------------------------------------------------

    /// <summary>Reads the list of notes again (and the open note, when someone else saved it and it has not been changed here).</summary>
    public async Task RefreshAsync()
    {
        OnPropertyChanged(nameof(HasSource)); OnPropertyChanged(nameof(NoSource));
        if (_source() is not { } source)
        {
            Notes.Clear(); OnPropertyChanged(nameof(HasNotes));
            ClearEditor();
            SetStatus(null);
            return;
        }
        IReadOnlyList<NoteInfo> list;
        try { list = await source.ListAsync(CancellationToken.None); }
        catch (NoteSourceException error) { Failed(error, Loc.Key("The notes could not be read: {0}")); return; }
        Merge(list);
        if (SelectedNote is { } open && open.Revision > _revision && !_dirty && !IsListening && !_isSaving) await ReloadOpenAsync();
    }

    private void Merge(IReadOnlyList<NoteInfo> list)
    {
        foreach (var info in list)
        {
            var row = Notes.FirstOrDefault(item => item.Id == info.Id);
            if (row is null) Notes.Add(new NoteRow(info));
            else if (row.Id != _openId || !_dirty) row.Update(info);          // what the person is writing is not replaced by an older line of the list
        }
        foreach (var gone in Notes.Where(row => list.All(info => info.Id != row.Id) && !(row.Id == _openId && _dirty)).ToArray())
        {
            if (ReferenceEquals(gone, SelectedNote)) { _openVersion++; ClearEditor(); }
            Notes.Remove(gone);
        }
        var order = Notes.OrderByDescending(row => row.UpdatedUtc).ToList();
        for (var i = 0; i < order.Count; i++)
        {
            var at = Notes.IndexOf(order[i]);
            if (at != i) Notes.Move(at, i);
        }
        OnPropertyChanged(nameof(HasNotes));
    }

    /// <summary>The server (or the folder) has changed: the list is read again for the new source.</summary>
    public async Task SourceChangedAsync()
    {
        if (IsListening) await StopRecordingAsync();
        _openVersion++;
        ClearEditor();
        Notes.Clear(); OnPropertyChanged(nameof(HasNotes));
        await RefreshAsync();
    }

    // ---- one note ---------------------------------------------------------------------------------------------------------------------

    /// <summary>Makes a new, empty note and opens it.</summary>
    [RelayCommand(CanExecute = nameof(CanSwitch))]
    public async Task NewNoteAsync()
    {
        if (_source() is not { } source || IsListening) return;
        await FlushAsync();
        NoteData created;
        try { created = await source.CreateAsync("", "", CancellationToken.None); }
        catch (NoteSourceException error) { Failed(error, Loc.Key("The note could not be created: {0}")); return; }
        var row = new NoteRow(new NoteInfo(created.Id, created.Title, "", created.UpdatedUtc, created.Revision));
        Notes.Insert(0, row);
        OnPropertyChanged(nameof(HasNotes));
        SelectedNote = row;
        await _opening;
    }

    private async Task OpenAsync(NoteRow? row)
    {
        var version = ++_openVersion;
        await FlushAsync();
        if (version != _openVersion) return;
        Notice = "";
        if (row is null || _source() is not { } source) { ClearEditor(); return; }
        try
        {
            var data = await source.GetAsync(row.Id, CancellationToken.None);
            if (version != _openVersion) return;
            Load(data);
        }
        catch (NoteSourceException error) when (error.Failure == NoteFailure.Missing)
        {
            if (version != _openVersion) return;
            Notes.Remove(row); OnPropertyChanged(nameof(HasNotes));
            ClearEditor();
            Notice = Loc.T("That note was deleted on another computer.");
        }
        catch (NoteSourceException error) { if (version == _openVersion) { ClearEditor(); Failed(error, Loc.Key("The note could not be opened: {0}")); } }
    }

    private void Load(NoteData data)
    {
        _loading = true;
        _openId = data.Id; _revision = data.Revision;
        Title = data.Title; Text = data.Text;
        _dirty = false; _failed = false;
        _loading = false;
        OnPropertyChanged(nameof(HasOpenNote)); OnPropertyChanged(nameof(NoNoteOpen));
        SetStatus(null);
    }

    private void ClearEditor()
    {
        _loading = true;
        _openId = null; _revision = 0; Title = ""; Text = ""; _dirty = false; _failed = false;
        _loading = false;
        StopSaveTimer();
        OnPropertyChanged(nameof(HasOpenNote)); OnPropertyChanged(nameof(NoNoteOpen));
        if (SelectedNote is not null && !Notes.Contains(SelectedNote)) SelectedNote = null;
        SetStatus(null);
    }

    private async Task ReloadOpenAsync()
    {
        if (_openId is not { } id || _source() is not { } source) return;
        try { Load(await source.GetAsync(id, CancellationToken.None)); }
        catch (NoteSourceException) { }
    }

    /// <summary>Deletes the open note (the window has asked first).</summary>
    public async Task DeleteOpenAsync()
    {
        if (_openId is not { } id || _source() is not { } source || IsListening) return;
        StopSaveTimer();
        try { await source.DeleteAsync(id, CancellationToken.None); }
        catch (NoteSourceException error) when (error.Failure == NoteFailure.Missing) { }
        catch (NoteSourceException error) { Failed(error, Loc.Key("The note could not be deleted: {0}")); return; }
        _openVersion++;
        _dirty = false;
        var row = Notes.FirstOrDefault(item => item.Id == id);
        ClearEditor();
        if (row is not null) Notes.Remove(row);
        SelectedNote = null;
        OnPropertyChanged(nameof(HasNotes));
        SetStatus(null);
    }

    // ---- saving -----------------------------------------------------------------------------------------------------------------------

    /// <summary>Raised when the server stopped answering during a call; the window then shows that the connection is gone.</summary>
    public event Action<string>? ConnectionLost;

    private void Edited()
    {
        if (_openId is null) return;
        _dirty = true;
        StartSaveTimer(SaveDelay);
    }

    /// <summary>Saves the open note now if it has changed. Everything that leaves the note (another note, a recording stopped, the window closing) waits for this.</summary>
    public async Task FlushAsync()
    {
        StopSaveTimer();
        for (var attempt = 0; attempt < 3 && _dirty && !_failed; attempt++) await SaveNowAsync();
    }

    public async Task SaveNowAsync()
    {
        StopSaveTimer();
        await _saving.WaitAsync();
        try { await SaveCoreAsync(); }
        finally { _saving.Release(); }
    }

    private async Task SaveCoreAsync()
    {
        if (!_dirty || _openId is not { } id || _source() is not { } source) return;
        var title = Title; var text = Text; var revision = _revision;
        _isSaving = true;
        SetStatus(() => Loc.T("Saving…"));
        try
        {
            var saved = await source.SaveAsync(id, title, text, revision, CancellationToken.None);
            _failed = false;
            if (_openId == id)
            {
                _revision = saved.Revision;
                if (Title == title && Text == text) _dirty = false;                // changed again meanwhile: it is saved once more
                else StartSaveTimer(SaveDelay);
            }
            UpdateRow(saved);
        }
        catch (NoteSourceException error) { await SaveFailedAsync(error, id, title, text); }
        finally { _isSaving = false; }
        SetStatus(null);
    }

    private void UpdateRow(NoteData saved)
    {
        var row = Notes.FirstOrDefault(item => item.Id == saved.Id);
        if (row is null) return;
        row.Update(new NoteInfo(saved.Id, saved.Title, NoteText.Preview(saved.Text), saved.UpdatedUtc, saved.Revision));
        var at = Notes.IndexOf(row);
        if (at > 0) Notes.Move(at, 0);
    }

    private async Task SaveFailedAsync(NoteSourceException error, Guid id, string title, string text)
    {
        if (_source() is not { } source) return;
        try
        {
            switch (error.Failure)
            {
                case NoteFailure.Changed:
                {
                    // Someone saved the note meanwhile. Nothing is overwritten and nothing is lost: what was written here is kept as a note of its own,
                    // and the note shows what the other computer saved.
                    var copy = await source.CreateAsync(CopyTitle(title), text, CancellationToken.None);
                    Notice = Loc.T("This note was changed on another computer. What you wrote was kept as a new note called \"{0}\".", copy.Title);
                    _dirty = false;
                    await RefreshAsync();
                    await ReloadOpenAsync();
                    break;
                }
                case NoteFailure.Missing:
                {
                    var again = await source.CreateAsync(title, text, CancellationToken.None);
                    Notice = Loc.T("This note was deleted on another computer. It was saved again.");
                    _openId = again.Id; _revision = again.Revision; _dirty = false;
                    var gone = Notes.FirstOrDefault(item => item.Id == id);
                    if (gone is not null) Notes.Remove(gone);
                    Notes.Insert(0, new NoteRow(new NoteInfo(again.Id, again.Title, NoteText.Preview(again.Text), again.UpdatedUtc, again.Revision)));
                    SelectedNoteSilently(Notes[0]);
                    OnPropertyChanged(nameof(HasNotes));
                    break;
                }
                default:
                    Failed(error, Loc.Key("The note could not be saved: {0}"));
                    _failed = true;
                    StartSaveTimer(RetryDelay);                                            // and tried again in a moment
                    break;
            }
        }
        catch (NoteSourceException second)
        {
            Failed(second, Loc.Key("The note could not be saved: {0}"));
            _failed = true;
            StartSaveTimer(RetryDelay);
        }
    }

    private string CopyTitle(string title)
    {
        var name = title.Length > 0 ? title : NoteText.Preview(Text, 30);
        return name.Length > 0 ? Loc.T("{0} (my version)", name) : Loc.T("Untitled note (my version)");
    }

    /// <summary>Makes a row the selected one without opening it again (it is the note that is already open).</summary>
    private void SelectedNoteSilently(NoteRow row)
    {
        _silentSelection = true;
        try { SelectedNote = row; }
        finally { _silentSelection = false; }
    }
    private void Failed(NoteSourceException error, string key)
    {
        var reason = error.Message;
        var failure = error.Failure;
        _failureText = () => Loc.T(key, Loc.Describe(reason));
        SetStatus(_failureText);
        if (failure == NoteFailure.Unreachable) ConnectionLost?.Invoke(reason);
    }

    private void StartSaveTimer(TimeSpan delay)
    {
        if (System.Windows.Application.Current?.Dispatcher is not { } dispatcher) return;       // no window (a test): SaveNowAsync is called by hand
        _saveTimer ??= new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, dispatcher);
        _saveTimer.Stop();
        _saveTimer.Interval = delay;
        _saveTimer.Tick -= OnSaveTimer;
        _saveTimer.Tick += OnSaveTimer;
        _saveTimer.Start();
    }

    private async void OnSaveTimer(object? sender, EventArgs args)
    {
        _saveTimer?.Stop();
        _failed = false;                                     // a retry
        await SaveNowAsync();
    }

    private void StopSaveTimer() => _saveTimer?.Stop();

    // ---- the keys ---------------------------------------------------------------------------------------------------------------------

    private string? RefuseKeys(KeyCombo combo) =>
        _hotkeys?.IsFree(combo, HotkeyAction) == false ? Loc.T("The keys {0} are used by another program, or already have a use here. Choose other keys.", combo.Label) : null;

    private void Capturing(bool on)
    {
        if (on) _paused ??= _hotkeys?.Pause();
        else { _paused?.Dispose(); _paused = null; }
    }

    private void OnKeysChosen(KeyCombo combo)
    {
        Change(settings => settings with { Hotkey = combo.Id });
        if (!_ownsHotkey) return;
        _hotkeys?.Claim(HotkeyAction, combo);
        RegisterHotkey();
    }

    private void RegisterHotkey()
    {
        if (!_ownsHotkey || _hotkeys is null) return;
        var combo = Keybind.Current;
        if (combo.IsNone) { _hotkeys.Unregister(HotkeyAction); HotkeyNote = ""; return; }
        HotkeyNote = _hotkeys.Register(HotkeyAction, combo, async () => await OnHotkeyAsync()) ? "" : Loc.T("The keys {0} are used by another program. Choose other keys.", combo.Label);
    }

    // ---- recording --------------------------------------------------------------------------------------------------------------------

    /// <summary>The keys of recording were pressed in some program: the little window shows while the note is recorded, so that the person sees it listens.</summary>
    private async Task OnHotkeyAsync()
    {
        if (IsListening) { await StopRecordingAsync(); return; }
        if (!IsOverlayVisible && Presenter is not null) { Presenter.ShowOverlay(this); IsOverlayVisible = true; _autoOverlay = true; }
        await StartRecordingAsync();
    }

    [RelayCommand]
    public async Task ToggleRecordingAsync()
    {
        if (IsListening) await StopRecordingAsync(); else await StartRecordingAsync();
    }

    /// <summary>Starts recording into the open note; with no note open, a new one is made first.</summary>
    public async Task StartRecordingAsync()
    {
        if (IsListening) return;
        if (_source() is null) { SetStatus(() => NoSourceText); return; }
        var engine = _engine();
        if (engine.Recognizer is not { } recognizer) { SetStatus(() => _engine().Note); return; }
        if (_openId is null)
        {
            await NewNoteAsync();
            if (_openId is null) return;
        }
        try { await _session.StartAsync(SelectedDevice?.Id ?? WindowsMicrophone.DefaultDevice, recognizer, () => LanguageChoice, Deliver); }
        catch (MicrophoneException error)
        {
            var reason = error.Message;
            SetStatus(() => Loc.Describe(reason));
            return;
        }
        IsListening = true;
        SetStatus(() => Loc.T("Listening…"));
        StartLevelTimer();
    }

    /// <summary>Stops recording. What was being said is still read and added, and the note is saved; this returns when that is done.</summary>
    public async Task StopRecordingAsync()
    {
        if (!IsListening && !_session.IsListening) { await _session.StopAsync(); return; }
        IsListening = false;
        StopLevelTimer();
        Level = 0;
        await _session.StopAsync(() => SetStatus(() => Loc.T("Finishing…")));
        await FlushAsync();
        SetStatus(null);
        if (_autoOverlay) HideOverlayCore();
    }

    /// <summary>The words of a phrase go to the end of the open note. (Called on a background thread; the note is changed on the window's.)</summary>
    private void Deliver(string words) => _onUi(() =>
    {
        Text = PhraseText.Append(Text, words);
        LastText = words;
    });

    // ---- the little window ------------------------------------------------------------------------------------------------------------

    /// <summary>The window's own close button: recording stops and the window goes.</summary>
    public async Task HideOverlayAsync()
    {
        if (!IsOverlayVisible) return;
        await StopRecordingAsync();
        HideOverlayCore();
    }

    private void HideOverlayCore()
    {
        if (!IsOverlayVisible) return;
        Presenter?.HideOverlay();
        IsOverlayVisible = false; _autoOverlay = false;
    }

    // ---- what the page shows ----------------------------------------------------------------------------------------------------------

    private void SetStatus(Func<string>? make)
    {
        _statusMake = make ?? DefaultStatus;
        Status = _statusMake();
    }

    private string DefaultStatus()
    {
        if (IsListening) return Loc.T("Listening…");
        if (_failed && _failureText is not null) return _failureText();            // a note that could not be saved says so until it is saved
        if (_source() is null) return NoSourceText;
        if (_openId is null) return IsOverlayVisible ? KeysHint() : "";
        return _dirty ? "" : Loc.T("All changes are saved");
    }

    private string KeysHint() => Keybind.Current.IsNone ? Loc.T("Press the button to start recording a note.") : Loc.T("Press the button or {0} to start recording a note.", Keybind.Current.Label);

    /// <summary>The meter of the little window follows the microphone while it listens.</summary>
    public void Tick() => Level = _session.Level;

    private void StartLevelTimer()
    {
        if (System.Windows.Application.Current?.Dispatcher is not { } dispatcher) return;
        _levelTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(100), System.Windows.Threading.DispatcherPriority.Normal, (_, _) => Tick(), dispatcher);
        _levelTimer.Start();
    }

    private void StopLevelTimer() => _levelTimer?.Stop();

    /// <summary>Builds the texts again after the interface language changed.</summary>
    public void RefreshTexts()
    {
        foreach (var device in Devices) device.NotifyLanguageChanged();
        foreach (var row in Notes) row.RefreshTexts();
        _languages = null; OnPropertyChanged(nameof(Languages));
        _secondLanguages = null; OnPropertyChanged(nameof(SecondLanguages));
        OnPropertyChanged(nameof(RecordButtonLabel)); OnPropertyChanged(nameof(NoSourceText));
        Keybind.RefreshTexts();
        RefreshAvailability();
        if (_statusMake is not null) Status = _statusMake();
        if (HotkeyNote.Length > 0) RegisterHotkey();
    }

    /// <summary>The window is closing: the microphone is let go of, the keys are given back and the note is saved as far as it can be without waiting.</summary>
    public void Dispose()
    {
        StopLevelTimer(); StopSaveTimer();
        _session.Dispose();
        _paused?.Dispose(); _paused = null;
        _hotkeys?.Unregister(HotkeyAction);
        if (_dirty && !_failed && _openId is { } id && _source() is { } source)
        {
            // A change the person made in the last moment is saved before the window goes. The save does not touch the window, so waiting a little for it is safe.
            var title = Title; var text = Text; var revision = _revision;
            try { Task.Run(async () => await source.SaveAsync(id, title, text, revision, CancellationToken.None)).Wait(TimeSpan.FromSeconds(3)); }
            catch (AggregateException) { /* the note could not be saved now; it was saved as far as the last autosave */ }
        }
    }
}