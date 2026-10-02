using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TriAsr.Application;
using TriAsr.Audio.Live;
using TriAsr.Audio.Recording;
using TriAsr.Domain;

namespace TriAsr.App;

/// <summary>What shows the little dictation window and listens for its hotkey. The windows provide it; tests use a stand-in.</summary>
public interface IDictationPresenter
{
    void ShowOverlay(DictationViewModel model);
    void HideOverlay();

    /// <returns>False when another program already uses the keys.</returns>
    bool RegisterHotkey(HotkeyChoice choice);
    void UnregisterHotkey();
}

/// <summary>Where the phrases are read and why that may not be possible.</summary>
/// <param name="Recognizer">Null when dictation cannot be used right now; <paramref name="Note"/> says why.</param>
public sealed record DictationEngine(ILiveRecognizer? Recognizer, string Note = "");

/// <summary>
/// Live dictation (ADR: live dictation): a little window that floats above the other programs; while it listens, every phrase that is spoken is read and typed where
/// the cursor is, in whatever program has the keyboard. The microphone's sound is cut into phrases at the pauses (<see cref="UtteranceDetector"/>), each phrase is read
/// by <see cref="ILiveRecognizer"/> (on this computer, or on the server a Client is connected to), and the words go to <see cref="ITextOutput"/>. One phrase is read at a time
/// and typed in the order it was spoken, so that a slow phrase never overtakes a quick one.
/// </summary>
public sealed partial class DictationViewModel : ObservableObject, IDisposable
{
    private static readonly LanguageOption AutoDetect = new("auto", Loc.Key("Auto-detect language"));
    private static readonly string TypeMethod = Loc.Key("Type the words"), PasteMethod = Loc.Key("Paste the words");

    private readonly IMicrophone _microphone;
    private readonly Func<DictationEngine> _engine;
    private readonly ITextOutput _output;
    private readonly DictationSettingsStore _store;
    private readonly Action<Action> _onUi;
    private readonly object _gate = new();
    private IDisposable? _capture;
    private UtteranceDetector? _detector;
    private Channel<Utterance>? _phrases;
    private Task _consumer = Task.CompletedTask;
    private CancellationTokenSource? _cancellation;
    private Func<string>? _statusMake;
    private DictationSettings _saved;
    private bool _restoring;
    private int _waiting;
    private volatile bool _problem;                // a problem is on show; it stays until the next phrase is read
    private IReadOnlyList<LanguageOption>? _languages;
    private System.Windows.Threading.DispatcherTimer? _timer;

    public DictationViewModel(IMicrophone microphone, Func<DictationEngine> engine, ITextOutput output, DictationSettingsStore store, Action<Action> onUi)
    {
        _microphone = microphone; _engine = engine; _output = output; _store = store; _onUi = onUi;
        _saved = store.Load();
        _restoring = true;
        SelectedLanguage = _saved.Language;
        SelectedHotkey = HotkeyChoice.Find(_saved.Hotkey);
        SelectedMethod = _saved.Method == "paste" ? PasteMethod : TypeMethod;
        RefreshDevices();
        _restoring = false;
        RefreshAvailability();
        SetStatus(null);
    }

    /// <summary>Set by the window that shows the little window and listens for the hotkey.</summary>
    public IDictationPresenter? Presenter { get; set; }

    public IReadOnlyList<LanguageOption> Languages => _languages ??= LanguageText.InOrder(LanguageCatalog.All).Prepend(AutoDetect).ToArray();
    public IReadOnlyList<HotkeyChoice> Hotkeys => HotkeyChoice.All;
    public IReadOnlyList<string> Methods { get; } = [TypeMethod, PasteMethod];
    public ObservableCollection<DeviceChoice> Devices { get; } = [];

    [ObservableProperty] private string _selectedLanguage = "auto";
    [ObservableProperty] private HotkeyChoice _selectedHotkey = HotkeyChoice.All[0];
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

    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));
    partial void OnHotkeyNoteChanged(string value) => OnPropertyChanged(nameof(HasHotkeyNote));
    partial void OnSelectedLanguageChanged(string value) => Save();
    partial void OnSelectedMethodChanged(string value) => Save();
    partial void OnSelectedDeviceChanged(DeviceChoice? value) => Save();
    partial void OnSelectedHotkeyChanged(HotkeyChoice value)
    {
        Save();
        if (_restoring) return;
        if (IsOverlayVisible) RegisterHotkey();                    // the new keys take over at once
        if (!IsListening && !_problem) SetStatus(null);            // and the hint names them
    }

    private string MethodId => SelectedMethod == PasteMethod ? "paste" : "type";

    private void Save()
    {
        if (_restoring) return;
        _saved = _saved with { Language = SelectedLanguage, Hotkey = SelectedHotkey.Id, Method = MethodId, Microphone = SelectedDevice?.Id ?? WindowsMicrophone.DefaultDevice };
        _store.Save(_saved);
    }

    /// <summary>The window was moved: the next time it appears where it was left.</summary>
    public void RememberPosition(double left, double top)
    {
        _saved = _saved with { Left = left, Top = top };
        _store.Save(_saved);
    }

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
        Presenter?.UnregisterHotkey();
        Presenter?.HideOverlay();
        IsOverlayVisible = false;
        HotkeyNote = "";
    }

    private void RegisterHotkey()
    {
        var choice = SelectedHotkey;
        HotkeyNote = Presenter?.RegisterHotkey(choice) == false ? Loc.T("The keys {0} are used by another program. Choose other keys.", choice.Label) : "";
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
        await _consumer.ConfigureAwait(true);                         // a stop that is still reading its last phrase has to finish first
        var engine = _engine();
        if (engine.Recognizer is not { } recognizer) { SetStatus(() => _engine().Note); return; }
        var phrases = Channel.CreateUnbounded<Utterance>(new UnboundedChannelOptions { SingleReader = true });
        var detector = new UtteranceDetector(phrase => phrases.Writer.TryWrite(phrase));
        var cancellation = new CancellationTokenSource();
        try { _capture = _microphone.Start(SelectedDevice?.Id ?? WindowsMicrophone.DefaultDevice, piece => detector.Feed(piece.Span), error => _onUi(() => Failed(error))); }
        catch (MicrophoneException error)
        {
            cancellation.Dispose();
            var reason = error.Message;
            SetStatus(() => Loc.Describe(reason));
            return;
        }
        lock (_gate) { _detector = detector; _phrases = phrases; _cancellation = cancellation; }
        _consumer = Task.Run(() => ReadAndTypeAsync(recognizer, phrases.Reader, cancellation.Token));
        _problem = false;
        IsListening = true;
        SetStatus(() => Loc.T("Listening…"));
        StartTimer();
    }

    /// <summary>Stops listening. What was being said is still read and typed, so nothing is lost; this returns when the last phrase has been dealt with.</summary>
    public async Task StopAsync()
    {
        Channel<Utterance>? phrases;
        UtteranceDetector? detector;
        lock (_gate) { phrases = _phrases; detector = _detector; _phrases = null; _detector = null; }
        if (!IsListening && phrases is null) { await _consumer.ConfigureAwait(true); return; }
        IsListening = false;
        StopTimer();
        _capture?.Dispose();                                          // waits for the last piece of sound
        _capture = null;
        detector?.Flush();
        phrases?.Writer.TryComplete();
        Level = 0;
        if (Volatile.Read(ref _waiting) > 0) SetStatus(() => Loc.T("Finishing…"));
        await _consumer.ConfigureAwait(true);
        _cancellation?.Dispose(); _cancellation = null;
        SetStatus(null);
    }

    private void Failed(Exception error)
    {
        if (!IsListening) return;
        var reason = error.Message;
        _ = StopAsync().ContinueWith(_ => _onUi(() => SetStatus(() => Loc.Describe(reason))), TaskScheduler.Default);
    }

    private async Task ReadAndTypeAsync(ILiveRecognizer recognizer, ChannelReader<Utterance> phrases, CancellationToken token)
    {
        try
        {
            await foreach (var phrase in phrases.ReadAllAsync(token).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _waiting);
                _problem = false;
                _onUi(() => { if (IsListening) SetStatus(() => Loc.T("Reading what you said…")); });
                try { await ReadOneAsync(recognizer, phrase, token).ConfigureAwait(false); }
                finally
                {
                    var left = Interlocked.Decrement(ref _waiting);
                    _onUi(() => { if (IsListening && left == 0 && !_problem) SetStatus(() => Loc.T("Listening…")); });
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReadOneAsync(ILiveRecognizer recognizer, Utterance phrase, CancellationToken token)
    {
        string text;
        var language = SelectedLanguage;
        var method = MethodId;
        try { text = PhraseText.Clean(await recognizer.RecognizeAsync(PhraseText.Wav(phrase.Pcm), language, token).ConfigureAwait(false)); }
        catch (OperationCanceledException) { throw; }
        catch (LiveException error) { var reason = error.Message; _problem = true; _onUi(() => SetStatus(() => Loc.Describe(reason))); return; }
        catch (Exception error) when (error is IOException or HttpRequestException or InvalidOperationException or UnauthorizedAccessException)
        {
            var reason = error.Message;
            _problem = true;
            _onUi(() => SetStatus(() => Loc.T("The phrase could not be recognised: {0}", Loc.Describe(reason))));
            return;
        }
        if (text.Length == 0 || PhraseText.IsPhantom(text, phrase.Speech)) return;
        try
        {
            var typed = text + (EndsWithoutSpaces(text) ? "" : " ");
            if (method == "paste") _output.Paste(typed); else _output.Type(typed);
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            var reason = error.Message;
            _problem = true;
            _onUi(() => SetStatus(() => reason));
            return;
        }
        _onUi(() => LastText = text);
    }

    /// <summary>Chinese, Japanese and Korean are written without a space between phrases.</summary>
    private static bool EndsWithoutSpaces(string text)
    {
        var c = text[^1];
        return c is >= '぀' and <= 'ヿ' or >= '㐀' and <= '鿿' or >= '가' and <= '힯' or >= '＀' and <= '￯' or '。' or '、';
    }

    // ---- what the window shows ---------------------------------------------------------------------------------------------------

    /// <summary>Refreshes the level of the meter; the window's timer calls it ten times a second while listening.</summary>
    public void Tick()
    {
        UtteranceDetector? detector;
        lock (_gate) detector = _detector;
        if (detector is null) return;
        Level = Math.Min(100, detector.Level * 600);
    }

    private void SetStatus(Func<string>? make)
    {
        _statusMake = make ?? (() => IsListening ? Loc.T("Listening…") : Loc.T("Press the button or {0} to start dictating.", SelectedHotkey.Label));
        Status = _statusMake();
    }

    /// <summary>Builds the texts again after the interface language changed.</summary>
    public void RefreshTexts()
    {
        foreach (var device in Devices) device.NotifyLanguageChanged();
        _languages = null; OnPropertyChanged(nameof(Languages));
        OnPropertyChanged(nameof(OverlayButtonLabel));
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
        Channel<Utterance>? phrases;
        lock (_gate) { phrases = _phrases; _phrases = null; _detector = null; }
        _capture?.Dispose(); _capture = null;
        phrases?.Writer.TryComplete();
        _cancellation?.Cancel();
        Presenter?.UnregisterHotkey();
    }
}
