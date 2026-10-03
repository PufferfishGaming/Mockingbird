using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TriAsr.Application;
using TriAsr.Audio.Recording;
using TriAsr.Domain;

namespace TriAsr.App;

/// <summary>Where the phrases are read and why that may not be possible.</summary>
/// <param name="Recognizer">Null when dictation cannot be used right now; <paramref name="Note"/> says why.</param>
public sealed record DictationEngine(ILiveRecognizer? Recognizer, string Note = "");

/// <summary>
/// Live dictation (ADR: live dictation): a little window that floats above the other programs; while it listens, every phrase that is spoken is read and typed where
/// the cursor is, in whatever program has the keyboard. The listening itself is a <see cref="LiveSession"/>; this puts the words on the keyboard (<see cref="ITextOutput"/>),
/// keeps what the person chose, and shows the little window and its keys.
/// </summary>
public sealed partial class DictationViewModel : ObservableObject, IDisposable, ILiveOverlay
{
    /// <summary>The name of dictation among the hotkeys of the program.</summary>
    public const string HotkeyAction = "dictation";

    /// <summary>The keys that start and stop dictation until the person chooses others.</summary>
    public static readonly KeyCombo DefaultKeys = new(KeyCombo.Control | KeyCombo.Alt, 0x20);

    private static readonly string TypeMethod = Loc.Key("Type the words"), PasteMethod = Loc.Key("Paste the words");

    private readonly IMicrophone _microphone;
    private readonly Func<DictationEngine> _engine;
    private readonly ITextOutput _output;
    private readonly DictationSettingsStore _store;
    private readonly Action<Action> _onUi;
    private readonly LiveSession _session;
    private Func<string>? _statusMake;
    private DictationSettings _saved;
    private bool _restoring;
    private IReadOnlyList<LanguageOption>? _languages, _secondLanguages;
    private IHotkeys? _hotkeys;
    private IDisposable? _paused;
    private System.Windows.Threading.DispatcherTimer? _timer;

    public DictationViewModel(IMicrophone microphone, Func<DictationEngine> engine, ITextOutput output, DictationSettingsStore store, Action<Action> onUi)
    {
        _microphone = microphone; _engine = engine; _output = output; _store = store; _onUi = onUi;
        _saved = store.Load();
        _session = new LiveSession(microphone, onUi);
        _session.Reading += () => SetStatus(() => Loc.T("Reading what you said…"));
        _session.Settled += () => SetStatus(() => Loc.T("Listening…"));
        _session.Problem += make => SetStatus(make);
        _session.MicrophoneFailed += make =>
        {
            if (!IsListening) return;
            _ = StopAsync().ContinueWith(_ => _onUi(() => SetStatus(make)), TaskScheduler.Default);
        };
        Keybind = new KeybindViewModel(KeyCombo.ParseOr(_saved.Hotkey, DefaultKeys), DefaultKeys, RefuseKeys, Capturing, OnKeysChosen);
        _restoring = true;
        (SelectedLanguage, SelectedSecondLanguage) = LiveLanguages.Split(_saved.Language);
        SelectedMethod = _saved.Method == "paste" ? PasteMethod : TypeMethod;
        RefreshDevices();
        _restoring = false;
        RefreshAvailability();
        SetStatus(null);
    }

    /// <summary>Set by the window that shows the little window.</summary>
    public IOverlayPresenter? Presenter { get; set; }

    /// <summary>Set by the window: the keys that work in every program. Dictation uses it while its little window is on show.</summary>
    public IHotkeys? Hotkeys
    {
        get => _hotkeys;
        set
        {
            _hotkeys = value;
            value?.Claim(HotkeyAction, Keybind.Current);
        }
    }

    /// <summary>The keys that start and stop dictation, and choosing others by pressing them.</summary>
    public KeybindViewModel Keybind { get; }

    public IReadOnlyList<LanguageOption> Languages => _languages ??= LiveLanguages.First();
    public IReadOnlyList<LanguageOption> SecondLanguages => _secondLanguages ??= LiveLanguages.Second(SelectedLanguage);
    public IReadOnlyList<string> Methods { get; } = [TypeMethod, PasteMethod];
    public ObservableCollection<DeviceChoice> Devices { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanChooseSecondLanguage))] private string _selectedLanguage = "auto";
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
    public string LanguageChoice => LiveLanguages.Join(SelectedLanguage, _selectedSecondLanguage);
    [ObservableProperty] private string _selectedMethod = TypeMethod;
    [ObservableProperty] private DeviceChoice? _selectedDevice;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanChoose))] private bool _isListening;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OverlayButtonLabel))] private bool _isOverlayVisible;
    [ObservableProperty] private double _level;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _lastText = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _hotkeyNote = "";

    public bool HasNote => Note.Length > 0;
    public bool HasHotkeyNote => HotkeyNote.Length > 0;
    public bool CanChoose => !IsListening;
    public bool IsAvailable => _engine().Recognizer is not null;
    public string OverlayButtonLabel => IsOverlayVisible ? Loc.T("Hide the dictation window") : Loc.T("Show the dictation window");

    ICommand ILiveOverlay.ToggleListeningCommand => ToggleListeningCommand;

    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));
    partial void OnHotkeyNoteChanged(string value) => OnPropertyChanged(nameof(HasHotkeyNote));
    partial void OnSelectedLanguageChanged(string value)
    {
        if (value == "auto" || value == _selectedSecondLanguage) { _selectedSecondLanguage = ""; OnPropertyChanged(nameof(SelectedSecondLanguage)); }
        _secondLanguages = null; OnPropertyChanged(nameof(SecondLanguages));
        Change(settings => settings with { Language = LanguageChoice });
    }
    partial void OnSelectedMethodChanged(string value) => Change(settings => settings with { Method = value == PasteMethod ? "paste" : "type" });
    partial void OnSelectedDeviceChanged(DeviceChoice? value) => Change(settings => settings with { Microphone = value?.Id ?? WindowsMicrophone.DefaultDevice });

    private string MethodId => SelectedMethod == PasteMethod ? "paste" : "type";

    /// <summary>Changes one thing the person chose. The file is read again first: Studio's dictation and the one of its Remote server page keep their choices in the same file, and the other may have changed something else meanwhile.</summary>
    private void Change(Func<DictationSettings, DictationSettings> change)
    {
        if (_restoring) return;
        _saved = change(_store.Load());
        _store.Save(_saved);
    }

    /// <summary>The window was moved: the next time it appears where it was left.</summary>
    public void RememberPosition(double left, double top) => Change(settings => settings with { Left = left, Top = top });

    public (double Left, double Top)? Position => _saved is { Left: { } left, Top: { } top } ? (left, top) : null;

    public void RefreshDevices()
    {
        var keep = SelectedDevice?.Id ?? _saved.Microphone;
        Devices.Clear();
        foreach (var device in _microphone.Devices()) Devices.Add(new DeviceChoice(device));
        SelectedDevice = Devices.FirstOrDefault(choice => choice.Id == keep) ?? Devices.FirstOrDefault();
    }

    /// <summary>Looks again at whether dictation can be used (a model was downloaded, a server was connected) and says why not.</summary>
    public void RefreshAvailability()
    {
        Note = _engine().Note;
        OnPropertyChanged(nameof(IsAvailable));
        if (!IsListening) SetStatus(null);
    }

    // ---- the keys ---------------------------------------------------------------------------------------------------------------------

    /// <summary>Why the keys cannot be chosen: another program has them. Null when they are free.</summary>
    private string? RefuseKeys(KeyCombo combo) =>
        _hotkeys?.IsFree(combo, HotkeyAction) == false ? Loc.T("The keys {0} are used by another program, or already have a use here. Choose other keys.", combo.Label) : null;

    /// <summary>The person is choosing keys: the keys that are on are switched off meanwhile, so that pressing one of them can be chosen too.</summary>
    private void Capturing(bool on)
    {
        if (on) _paused ??= _hotkeys?.Pause();
        else { _paused?.Dispose(); _paused = null; }
    }

    private void OnKeysChosen(KeyCombo combo)
    {
        Change(settings => settings with { Hotkey = combo.Id });
        _hotkeys?.Claim(HotkeyAction, combo);
        if (IsOverlayVisible) RegisterHotkey();                    // the new keys take over at once
        if (!IsListening && !_session.HasProblem) SetStatus(null); // and the hint names them
    }

    private void RegisterHotkey()
    {
        var combo = Keybind.Current;
        if (_hotkeys is null) return;
        HotkeyNote = _hotkeys.Register(HotkeyAction, combo, async () => await ToggleListeningAsync()) ? "" : Loc.T("The keys {0} are used by another program. Choose other keys.", combo.Label);
    }

    // ---- the little window ------------------------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task ToggleOverlayAsync()
    {
        if (IsOverlayVisible) await HideOverlayAsync(); else ShowOverlay();
    }

    public void ShowOverlay()
    {
        if (Presenter is null || IsOverlayVisible) return;
        Presenter.ShowOverlay(this);
        IsOverlayVisible = true;
        RegisterHotkey();
        SetStatus(null);
    }

    public async Task HideOverlayAsync()
    {
        if (!IsOverlayVisible) return;
        await StopAsync();
        _hotkeys?.Unregister(HotkeyAction);
        Presenter?.HideOverlay();
        IsOverlayVisible = false;
        HotkeyNote = "";
    }

    /// <summary>The window's own close button, and the hotkey: pressing it starts or stops dictation.</summary>
    [RelayCommand]
    public async Task ToggleListeningAsync()
    {
        if (IsListening) await StopAsync(); else await StartAsync();
    }

    // ---- listening --------------------------------------------------------------------------------------------------------------------

    public async Task StartAsync()
    {
        if (IsListening) return;
        var engine = _engine();
        if (engine.Recognizer is not { } recognizer) { SetStatus(() => _engine().Note); return; }
        try { await _session.StartAsync(SelectedDevice?.Id ?? WindowsMicrophone.DefaultDevice, recognizer, () => LanguageChoice, Deliver); }
        catch (MicrophoneException error)
        {
            var reason = error.Message;
            SetStatus(() => Loc.Describe(reason));
            return;
        }
        IsListening = true;
        SetStatus(() => Loc.T("Listening…"));
        StartTimer();
    }

    /// <summary>Stops listening. What was being said is still read and typed, so nothing is lost; this returns when the last phrase has been dealt with.</summary>
    public async Task StopAsync()
    {
        if (!IsListening && !_session.IsListening) { await _session.StopAsync(); return; }
        IsListening = false;
        StopTimer();
        Level = 0;
        await _session.StopAsync(() => SetStatus(() => Loc.T("Finishing…")));
        SetStatus(null);
    }

    /// <summary>Types (or pastes) the words of a phrase, with a space after them for the next one.</summary>
    private void Deliver(string text)
    {
        var typed = text + (PhraseText.IsUnspacedScript(text[^1]) ? "" : " ");
        if (MethodId == "paste") _output.Paste(typed); else _output.Type(typed);
        _onUi(() => LastText = text);
    }


    // ---- what the window shows ---------------------------------------------------------------------------------------------------

    /// <summary>Refreshes the level of the meter; the window's timer calls it ten times a second while listening.</summary>
    public void Tick() => Level = _session.Level;

    private void SetStatus(Func<string>? make)
    {
        _statusMake = make ?? (() => IsListening ? Loc.T("Listening…") : Keybind.Current.IsNone ? Loc.T("Press the button to start dictating.") : Loc.T("Press the button or {0} to start dictating.", Keybind.Current.Label));
        Status = _statusMake();
    }

    /// <summary>Builds the texts again after the interface language changed.</summary>
    public void RefreshTexts()
    {
        foreach (var device in Devices) device.NotifyLanguageChanged();
        _languages = null; OnPropertyChanged(nameof(Languages));
        _secondLanguages = null; OnPropertyChanged(nameof(SecondLanguages));
        OnPropertyChanged(nameof(OverlayButtonLabel));
        Keybind.RefreshTexts();
        RefreshAvailability();
        if (_statusMake is not null) Status = _statusMake();
        if (HotkeyNote.Length > 0) RegisterHotkey();
    }

    private void StartTimer()
    {
        if (System.Windows.Application.Current?.Dispatcher is not { } dispatcher) return;       // no window (a test): Tick is called by hand
        _timer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(100), System.Windows.Threading.DispatcherPriority.Normal, (_, _) => Tick(), dispatcher);
        _timer.Start();
    }

    private void StopTimer() => _timer?.Stop();

    /// <summary>The window is closing: the microphone is let go of and what is still being read is dropped. (Nothing waits here: the window's own thread must stay free.)</summary>
    public void Dispose()
    {
        StopTimer();
        _session.Dispose();
        _paused?.Dispose(); _paused = null;
        _hotkeys?.Unregister(HotkeyAction);
    }
}
