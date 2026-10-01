using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TriAsr.App;
public sealed partial class ShellViewModel : IDisposable
{
    private DispatcherTimer? _terminalTimer;
    private long _activityClearedAt, _consoleClearedAt, _lastActivitySequence = -1, _lastConsoleSequence = -1;
    private string _activityText = "", _consoleText = "";
    private readonly List<string> _commandHistory = [];
    private int _historyPosition;
    public bool IsTerminalPage => SelectedPage?.Name == "Terminal";
    public IReadOnlyList<string> TerminalViews { get; } = ["Activity", "PowerShell"];
    [ObservableProperty] private string _terminalView = "Activity";
    [ObservableProperty] private string _terminalInput = "";
    [ObservableProperty] private bool _terminalAutoScroll = true;
    [ObservableProperty] private bool _terminalSubmitting;
    [ObservableProperty] private string _terminalStatus = "PowerShell is stopped. Run a command or press Start PowerShell.";
    [ObservableProperty] private string _terminalDirectory = "";
    [ObservableProperty] private string _terminalSendLabel = "Run command";
    public string TerminalOutput => TerminalView == "PowerShell" ? _consoleText : _activityText;
    partial void OnTerminalViewChanged(string value) { OnPropertyChanged(nameof(TerminalOutput)); RefreshTerminal(); }
    partial void OnTerminalInputChanged(string value) => SubmitTerminalCommand.NotifyCanExecuteChanged();
    partial void OnTerminalSubmittingChanged(bool value) => SubmitTerminalCommand.NotifyCanExecuteChanged();
    private void InitializeTerminal()
    {
        TerminalDirectory = storage.Root;
        PropertyChanged += ObserveActivity;
        _terminalTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _terminalTimer.Tick += (_, _) => RefreshTerminal(); _terminalTimer.Start();
        activity.Append("app", "Workspace: " + storage.Root + " · model repository: " + runtimes.ModelRoot);
    }
    private void ObserveActivity(object? sender, PropertyChangedEventArgs args)
    {
        var message = args.PropertyName switch
        {
            nameof(Status) => Status, nameof(ModelProgress) => ModelProgress, nameof(BackendProgress) => BackendProgress,
            nameof(BenchmarkProgress) => BenchmarkProgress, nameof(TranscriptionStage) => TranscriptionStage,
            nameof(SystemSummary) => SystemSummary, _ => null
        };
        if (!string.IsNullOrWhiteSpace(message)) activity.Append(args.PropertyName!, message);
    }
    public void RefreshTerminal()
    {
        TerminalDirectory = string.IsNullOrEmpty(terminal.CurrentDirectory) ? storage.Root : terminal.CurrentDirectory;
        TerminalStatus = terminal.IsRunning ? terminal.IsBusy ? "Command running · send input if it requests a response" : "PowerShell ready · variables and current folder are preserved" : "PowerShell stopped · start a new session";
        TerminalSendLabel = terminal.IsBusy ? "Send input" : "Run command";
        if (!IsTerminalPage) return;
        if (_lastActivitySequence != activity.Sequence)
        {
            _activityText = string.Join(Environment.NewLine, activity.Snapshot(_activityClearedAt).Select(entry => entry.Display));
            _lastActivitySequence = activity.Sequence; if (TerminalView == "Activity") OnPropertyChanged(nameof(TerminalOutput));
        }
        if (_lastConsoleSequence != terminal.Output.Sequence)
        {
            _consoleText = string.Join(Environment.NewLine, terminal.Output.Snapshot(_consoleClearedAt).Select(entry => entry.Message));
            _lastConsoleSequence = terminal.Output.Sequence; if (TerminalView == "PowerShell") OnPropertyChanged(nameof(TerminalOutput));
        }
    }
    private bool CanSubmitTerminal() => !TerminalSubmitting && !string.IsNullOrWhiteSpace(TerminalInput);
    [RelayCommand(CanExecute = nameof(CanSubmitTerminal))]
    private async Task SubmitTerminalAsync()
    {
        TerminalSubmitting = true;
        try
        {
            var command = TerminalInput; TerminalInput = ""; TerminalView = "PowerShell";
            if (!terminal.IsRunning) await terminal.StartAsync(storage.Root);
            if (terminal.IsBusy) await terminal.SendInputAsync(command);
            else
            {
                _commandHistory.Add(command); if (_commandHistory.Count > 100) _commandHistory.RemoveAt(0); _historyPosition = _commandHistory.Count;
                await terminal.RunCommandAsync(command);
            }
        }
        catch (Exception error) { ReportError("Terminal command failed", error.Message); }
        finally { TerminalSubmitting = false; RefreshTerminal(); }
    }
    [RelayCommand] private async Task StartTerminalAsync()
    {
        try { TerminalView = "PowerShell"; await terminal.StartAsync(storage.Root); RefreshTerminal(); }
        catch (Exception error) { ReportError("PowerShell could not start", error.Message); }
    }
    [RelayCommand] private async Task StopTerminalAsync()
    {
        try { await terminal.StopAsync(); RefreshTerminal(); }
        catch (Exception error) { ReportError("PowerShell could not stop", error.Message); }
    }
    [RelayCommand] private void ClearTerminal()
    {
        if (TerminalView == "PowerShell") { _consoleClearedAt = terminal.Output.Sequence; _lastConsoleSequence = -1; }
        else { _activityClearedAt = activity.Sequence; _lastActivitySequence = -1; }
        RefreshTerminal();
    }
    [RelayCommand] private void CopyTerminal()
    {
        try { if (TerminalOutput.Length > 0) Clipboard.SetText(TerminalOutput); }
        catch (Exception error) { ReportError("Could not copy terminal output", error.Message); }
    }
    public void RecallTerminalCommand(int direction)
    {
        if (terminal.IsBusy || _commandHistory.Count == 0 || TerminalInput.Contains('\n')) return;
        _historyPosition = Math.Clamp(_historyPosition + direction, 0, _commandHistory.Count);
        TerminalInput = _historyPosition == _commandHistory.Count ? "" : _commandHistory[_historyPosition];
    }
    public void Dispose() { _terminalTimer?.Stop(); PropertyChanged -= ObserveActivity; terminal.Dispose(); }
}
