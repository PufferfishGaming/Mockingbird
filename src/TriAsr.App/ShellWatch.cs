using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TriAsr.Domain;
using TriAsr.Export;
using TriAsr.Infrastructure;

namespace TriAsr.App;

/// <summary>Watch folder: recordings added to a chosen folder are transcribed automatically, one after another, and saved next to the recording.</summary>
public sealed partial class ShellViewModel
{
    public const string WatchAsText = "Text (.txt)";
    public const string WatchAsSubtitles = "Subtitles (.srt)";
    public const string WatchProjectOnly = "Project only";
    public IReadOnlyList<string> WatchOutputs { get; } = [WatchAsText, WatchAsSubtitles, WatchProjectOnly];
    [ObservableProperty] private bool _watchEnabled;
    [ObservableProperty] private string _watchFolder = "";
    [ObservableProperty] private string _watchLanguage = "auto";
    [ObservableProperty] private string _watchOutput = WatchAsText;
    [ObservableProperty] private string _watchStatus = "Off";
    [ObservableProperty] private bool _isWatchBusy;
    public bool HasWatchFolder => WatchFolder.Length > 0;
    private FolderWatcher? _watcher;
    private readonly SemaphoreSlim _watchGate = new(1, 1);
    private string _watchCurrent = "";
    private bool _watchBatch;

    private void RestoreWatchSettings(AppSettings settings)
    {
        _watchBatch = true;
        WatchFolder = settings.WatchFolder; WatchEnabled = settings.WatchEnabled && settings.WatchFolder.Length > 0;
        WatchLanguage = settings.WatchLanguage; WatchOutput = settings.WatchOutput;
        _watchBatch = false;
    }

    /// <summary>Called from the folder picker: watching a folder you just chose is what you asked for, so it also switches watching on.</summary>
    public void SetWatchFolder(string path)
    {
        _watchBatch = true;
        WatchFolder = Path.GetFullPath(path); WatchEnabled = true;
        _watchBatch = false;
        Persist();
        _ = RestartWatchAsync();
    }

    partial void OnWatchFolderChanged(string value) { OnPropertyChanged(nameof(HasWatchFolder)); if (_initialized && !_watchBatch) { Persist(); _ = RestartWatchAsync(); } }
    partial void OnWatchEnabledChanged(bool value) { if (_initialized && !_watchBatch) { Persist(); _ = RestartWatchAsync(); } }
    partial void OnWatchLanguageChanged(string value) { if (_initialized) Persist(); }
    partial void OnWatchOutputChanged(string value) { if (_initialized) Persist(); }

    [RelayCommand]
    private void OpenWatchFolder()
    {
        if (!Directory.Exists(WatchFolder)) { ReportError("Folder not found", "The watched folder does not exist any more. Choose it again."); return; }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{WatchFolder}\"") { UseShellExecute = true });
    }

    /// <summary>(Re)starts the watcher to match the settings. Watching only recordings added after it is switched on is deliberate: see <see cref="WatchLedger"/>.</summary>
    private async Task RestartWatchAsync()
    {
        await _watchGate.WaitAsync();
        try
        {
            if (_watcher is { } old) { _watcher = null; await old.DisposeAsync(); }
            if (!WatchEnabled) { WatchStatus = "Off"; return; }
            if (WatchFolder.Length == 0 || !Directory.Exists(WatchFolder)) { WatchStatus = "The folder was not found. Choose it again."; return; }
            var ledger = await WatchLedger.LoadAsync(Path.Combine(storage.Root, "Config", "watch-ledger.json"));
            await ledger.EnsureBaselineAsync(WatchFolder);
            var watcher = new FolderWatcher(WatchFolder, ledger, ProcessWatchedFileAsync);
            watcher.Changed += () => OnUi(RefreshWatchStatus);
            watcher.Start();
            _watcher = watcher;
            RefreshWatchStatus();
        }
        catch (Exception error)
        {
            WatchStatus = "Cannot watch this folder: " + error.Message;
            logger.LogWarning(error, "Watch folder could not start: {ErrorType}", error.GetType().Name);
        }
        finally { _watchGate.Release(); }
    }

    private void RefreshWatchStatus()
    {
        if (_watcher is null) return;
        var waiting = _watcher.Waiting;
        WatchStatus = IsWatchBusy
            ? $"Transcribing {_watchCurrent}" + (waiting > 1 ? $" · {waiting - 1} more waiting" : "")
            : waiting == 0 ? $"Watching {WatchFolder}" : $"Watching {WatchFolder} · {waiting} recording{(waiting == 1 ? "" : "s")} arriving";
    }

    private static void OnUi(Action action)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess()) dispatcher.Invoke(action);
        else action();
    }

    private async Task<WatchOutcome> ProcessWatchedFileAsync(string path, CancellationToken token)
    {
        var name = Path.GetFileName(path);
        var language = WatchLanguage;
        var missing = Array.Empty<string>();
        OnUi(() => missing = MissingRequiredModelsFor(language));
        if (missing.Length > 0)
        {
            OnUi(() =>
            {
                WatchEnabled = false;
                WatchStatus = "Paused: a speech model is missing. Download it on Models, then switch watching back on.";
                ReportError("Watch folder paused", $"{name} arrived, but these models are not downloaded: {string.Join(", ", missing)}.\nThe recording stays waiting and is transcribed when watching is switched on again.");
            });
            return WatchOutcome.Retry;
        }
        OnUi(() => { _watchCurrent = name; IsWatchBusy = true; RefreshWatchStatus(); });
        try
        {
            using var awake = SleepGuard.Begin("Mockingbird Studio is transcribing a watched recording");
            var job = await queue.EnqueueAsync(path, language, token);
            job = await pipeline.RunAsync(job, token);
            if (token.IsCancellationRequested) return WatchOutcome.Retry;
            if (job.State != JobState.Complete) return WatchOutcome.Failed; // the job's own error already reached the alert and the project list
            string? saved = null;
            try { saved = await ExportWatchedAsync(job, path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            { OnUi(() => ReportError("Could not save the transcript next to the recording", $"{name}: {error.Message}\nThe transcript is still in Projects.")); }
            OnUi(() => Status = $"Transcribed {name}" + (saved is null ? "" : " · saved " + Path.GetFileName(saved)));
            return WatchOutcome.Done;
        }
        catch (OperationCanceledException) { return WatchOutcome.Retry; }
        catch (Exception error)
        {
            OnUi(() => ReportError("Watched recording failed", $"{name}: {error.Message}"));
            return WatchOutcome.Failed;
        }
        finally { OnUi(() => { IsWatchBusy = false; RefreshWatchStatus(); }); }
    }

    /// <summary>Writes the transcript beside the recording in the chosen format, never over an existing file. Null when the choice is "Project only".</summary>
    private async Task<string?> ExportWatchedAsync(TranscriptionJob job, string recording)
    {
        if (WatchOutput == WatchProjectOnly) return null;
        var transcript = await stages.LoadReviewAsync(job.Id);
        var mode = SelectedExportMode;
        var exported = mode == "Readable" ? TranscriptExporter.ReadableCopy(transcript) : transcript;
        // Subtitles need native timestamps; a transcript without them is saved as text instead of failing.
        var extension = WatchOutput == WatchAsSubtitles && exported.Regions.All(region => region.NativeTimestamps) ? ".srt" : ".txt";
        var target = WatchRules.UniqueSibling(Path.ChangeExtension(recording, extension));
        await TranscriptExporter.SaveAsync(exported, target);
        await records.SaveAsync(new("exports", Guid.NewGuid().ToString("N"), JsonSerializer.Serialize(new { Path = target, Mode = mode, AtUtc = DateTimeOffset.UtcNow, Source = "watch-folder" }), job.Id));
        return target;
    }

    /// <summary>Stops watching without waiting; the recording being transcribed is left for the next start.</summary>
    public void StopWatchingForExit() { if (_watcher is { } watcher) { _watcher = null; _ = watcher.DisposeAsync().AsTask(); } }
}
