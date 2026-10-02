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
    // Stored in settings and compared as written, so the values stay English; the lists below show them translated.
    public static readonly string WatchAsText = Loc.Key("Text (.txt)");
    public static readonly string WatchAsSubtitles = Loc.Key("Subtitles (.srt)");
    public static readonly string WatchProjectOnly = Loc.Key("Project only");
    public IReadOnlyList<string> WatchOutputs { get; } = [WatchAsText, WatchAsSubtitles, WatchProjectOnly];
    [ObservableProperty] private bool _watchEnabled;
    [ObservableProperty] private string _watchFolder = "";
    [ObservableProperty] private string _watchLanguage = "auto";
    [ObservableProperty] private string _watchOutput = WatchAsText;
    [ObservableProperty] private string _watchStatus = Loc.Key("Off");
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
        if (!Directory.Exists(WatchFolder)) { ReportError(T("Folder not found"), T("The watched folder does not exist any more. Choose it again.")); return; }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{WatchFolder}\"") { UseShellExecute = true });
    }

    /// <summary>(Re)starts the watcher to match the settings. Watching only recordings added after it is switched on is deliberate: see <see cref="WatchLedger"/>.</summary>
    private async Task RestartWatchAsync()
    {
        await _watchGate.WaitAsync();
        try
        {
            if (_watcher is { } old) { _watcher = null; await old.DisposeAsync(); }
            if (!WatchEnabled) { WatchStatus = T("Off"); return; }
            if (WatchFolder.Length == 0 || !Directory.Exists(WatchFolder)) { WatchStatus = T("The folder was not found. Choose it again."); return; }
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
            WatchStatus = T("Cannot watch this folder: {0}", Loc.Describe(error.Message));
            logger.LogWarning(error, "Watch folder could not start: {ErrorType}", error.GetType().Name);
        }
        finally { _watchGate.Release(); }
    }

    private void RefreshWatchStatus()
    {
        if (_watcher is null) return;
        var waiting = _watcher.Waiting;
        WatchStatus = IsWatchBusy
            ? T("Transcribing {0}", _watchCurrent) + (waiting == 2 ? " · " + T("1 more recording waiting") : waiting > 2 ? " · " + T("{0} more recordings waiting", waiting - 1) : "")
            : waiting == 0 ? T("Watching {0}", WatchFolder)
            : waiting == 1 ? T("Watching {0} · 1 recording arriving", WatchFolder)
            : T("Watching {0} · {1} recordings arriving", WatchFolder, waiting);
    }

    private static void OnUi(Action action) => RemoteSession.OnUi(action);

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
                WatchStatus = T("Paused: a speech model is missing. Download it on Models, then switch watching back on.");
                ReportError(T("Watch folder paused"), T("{0} arrived, but these models are not downloaded: {1}.\nThe recording stays waiting and is transcribed when watching is switched on again.", name, string.Join(", ", missing)));
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
            { OnUi(() => ReportError(T("Could not save the transcript next to the recording"), T("{0}: {1}\nThe transcript is still in Projects.", name, Loc.Describe(error.Message)))); }
            OnUi(() => Status = saved is null ? T("Transcribed {0}", name) : T("Transcribed {0} · saved {1}", name, Path.GetFileName(saved)));
            return WatchOutcome.Done;
        }
        catch (OperationCanceledException) { return WatchOutcome.Retry; }
        catch (Exception error)
        {
            OnUi(() => ReportError(T("Watched recording failed"), T("{0}: {1}", name, Loc.Describe(error.Message))));
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
