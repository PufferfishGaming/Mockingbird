using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TriAsr.Audio.Recording;

namespace TriAsr.App;

/// <summary>One choice in the list of microphones. The first is whatever Windows uses by default; the others carry the name the device gives itself.</summary>
public sealed partial class DeviceChoice(InputDevice device) : ObservableObject
{
    public int Id { get; } = device.Id;
    public string Label => device.Name.Length > 0 ? device.Name : Loc.T("Default microphone");
    public void NotifyLanguageChanged() => OnPropertyChanged(nameof(Label));
}

/// <summary>
/// Recording from the microphone in the program's own window: Studio's New transcription page and the Client's New tab both show the same card.
/// A finished recording is saved as a WAV file in the Recordings folder of the data folder and handed on as the file to transcribe or to send; nothing is
/// recorded unless the button is pressed, and nothing leaves the computer from here.
/// </summary>
public sealed partial class RecorderViewModel : ObservableObject, IDisposable
{
    private readonly IMicrophone _microphone;
    private readonly Action<Action> _onUi;
    private readonly RecordingSession _session;
    private System.Windows.Threading.DispatcherTimer? _timer;
    private Func<string>? _statusMake;

    public RecorderViewModel(IMicrophone microphone, string folder, Action<Action> onUi)
    {
        _microphone = microphone; _onUi = onUi;
        _session = new RecordingSession(microphone, folder);
        _session.Failed += error => _onUi(() => Failed(error));
        RefreshDevices();
    }

    public ObservableCollection<DeviceChoice> Devices { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRecord))] private DeviceChoice? _selectedDevice;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanChooseDevice)), NotifyPropertyChangedFor(nameof(CanRecord))] private bool _isRecording;
    [ObservableProperty] private string _elapsed = "0:00";
    [ObservableProperty] private double _level;
    [ObservableProperty] private string _status = "";

    public bool CanChooseDevice => !IsRecording && Devices.Count > 0;
    public bool CanRecord => IsRecording || SelectedDevice is not null;
    public string Folder => _session.Folder;

    /// <summary>Shows a folder in Explorer. Only a test changes it.</summary>
    public Action<string> ShowFolder { get; set; } = folder =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });

    /// <summary>Opens the folder the recordings are saved in (made first, if nothing was recorded yet).</summary>
    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            ShowFolder(Folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            SetStatus(() => Loc.T("The recordings folder could not be opened: {0}", Loc.Describe(error.Message)));
        }
    }

    /// <summary>Raised on the window's thread when a recording was stopped and saved. The file is complete.</summary>
    public event Action<RecordedFile>? Recorded;

    public void RefreshDevices()
    {
        var keep = SelectedDevice?.Id;
        Devices.Clear();
        foreach (var device in _microphone.Devices()) Devices.Add(new DeviceChoice(device));
        SelectedDevice = Devices.FirstOrDefault(choice => choice.Id == keep) ?? Devices.FirstOrDefault();
        OnPropertyChanged(nameof(CanChooseDevice));
        if (!IsRecording) SetStatus(Devices.Count == 0 ? () => Loc.T("No microphone was found. Connect one, or check that it is enabled in the Windows sound settings.") : null);
    }

    [RelayCommand]
    private void ToggleRecording()
    {
        if (IsRecording) Stop(); else Start();
    }

    private void Start()
    {
        if (SelectedDevice is null) { RefreshDevices(); if (SelectedDevice is null) return; }
        try
        {
            _session.Start(SelectedDevice.Id);
            IsRecording = true; Elapsed = "0:00"; Level = 0;
            SetStatus(() => Loc.T("Recording…"));
            StartTimer();
        }
        catch (Exception error) when (error is MicrophoneException or IOException or UnauthorizedAccessException)
        {
            SetStatus(() => Loc.Describe(error.Message));
        }
    }

    private void Stop()
    {
        StopTimer();
        try
        {
            var file = _session.Stop();
            IsRecording = false; Level = 0; Elapsed = Clock(file.Duration);
            if (file.Silent || file.Duration < TimeSpan.FromSeconds(0.5))
            {
                SetStatus(() => Loc.T("No sound was recorded. Windows may be blocking the microphone: open Settings, Privacy & security, Microphone, and allow desktop apps to use it. Also check that the microphone is not muted."));
                try { File.Delete(file.Path); } catch (IOException) { }
                return;
            }
            SetStatus(() => Loc.T("Recording saved: {0} ({1})", Path.GetFileName(file.Path), Clock(file.Duration)));
            Recorded?.Invoke(file);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            IsRecording = false; Level = 0;
            SetStatus(() => Loc.T("The recording could not be saved: {0}", Loc.Describe(error.Message)));
        }
    }

    /// <summary>The microphone went away while recording: what was recorded so far is kept.</summary>
    private void Failed(Exception error)
    {
        if (!IsRecording) return;
        Stop();
        var reason = Loc.Describe(error.Message);
        SetStatus(() => Loc.T("The recording stopped: {0}", reason));
    }

    /// <summary>Refreshes the time and the level; the window's timer calls it ten times a second while recording.</summary>
    public void Tick()
    {
        if (!IsRecording) return;
        Elapsed = Clock(_session.Duration);
        Level = _session.Level * 100;
        if (_session.HeardNothing)
            SetStatus(() => Loc.T("No sound is coming from the microphone. Windows may be blocking it (Settings, Privacy & security, Microphone, desktop apps), or it is muted."));
    }

    private static string Clock(TimeSpan time) => time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";

    private void SetStatus(Func<string>? make) { _statusMake = make; Status = make?.Invoke() ?? ""; }

    /// <summary>Builds the texts again after the interface language changed.</summary>
    public void RefreshTexts()
    {
        foreach (var choice in Devices) choice.NotifyLanguageChanged();
        Status = _statusMake?.Invoke() ?? "";
    }

    private void StartTimer()
    {
        if (System.Windows.Application.Current?.Dispatcher is not { } dispatcher) return; // no window (a test): Tick is called by hand
        _timer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(100), System.Windows.Threading.DispatcherPriority.Normal, (_, _) => Tick(), dispatcher);
        _timer.Start();
    }

    private void StopTimer() => _timer?.Stop();

    /// <summary>The window is closing: the recording so far is saved, not lost.</summary>
    public void StopForExit()
    {
        if (!IsRecording) return;
        try { Stop(); } catch (Exception error) when (error is IOException or InvalidOperationException) { }
    }

    public void Dispose() { StopTimer(); _session.Dispose(); }
}
