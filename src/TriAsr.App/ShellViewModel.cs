using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using TriAsr.Application;
using TriAsr.Domain;
using TriAsr.Hardware;
using System.Text.Json;
using TriAsr.Infrastructure;

namespace TriAsr.App;

public sealed record NavigationItem(string Name, string Title, string Description, string Icon, string EmptyTitle, string EmptyDescription)
{
    public bool IsSettings => Name == "Settings";
}

public sealed partial class ShellViewModel(SettingsStore store, ThemeManager themes, ILogger<ShellViewModel> logger,
    IJobRepository repository, AudioJobQueue queue, RuntimePaths runtimes, HardwareProfiler hardware, IStoragePaths storage,
    TranscriptionPipeline pipeline, LocalTranscriptionStages stages, IJobWorkspace workspace, ModelStore models, LocalOptimizer optimizer, IRecordRepository records,
    ActivityFeed activity, InteractiveTerminal terminal, UpdateService updates, ResourceGovernor governor) : ObservableObject
{
    public IReadOnlyList<NavigationItem> Navigation { get; } =
    [
        new("New Transcription", "New transcription", "Import audio or video and choose your recognition settings.", "M12,3 L12,21 M3,12 L21,12", "Your next recording starts here", "Choose a recording to begin."),
        new("Projects", "Your projects", "Recordings, transcripts and their original evidence, in one place.", "M3,6 L10,6 L12,8 L21,8 L21,20 L3,20 Z", "A clean workspace", "Your saved transcription projects will appear here."),
        new("Queue", "Processing queue", "Track each recording from preprocessing to final transcript.", "M4,5 L20,5 M4,12 L20,12 M4,19 L20,19", "No active jobs", "New recordings will join the local processing queue."),
        new("Review", "Review transcript", "Listen, compare alternatives and resolve uncertain wording.", "M3,12 L9,18 L21,5", "Nothing to review yet", "Disagreements between speech engines will appear here."),
        new("Models", "Your local engines", "Manage speech and correction models on this computer.", "M12,2 L22,7 L22,17 L12,22 L2,17 L2,7 Z M2,7 L12,12 L22,7 M12,12 L12,22", "Models will be managed here", "Download or select a local model."),
        new("Languages", "More languages, locally", "Download shared multilingual models and see language coverage.", "M2,12 A10,10 0 1 0 22,12 A10,10 0 1 0 2,12 M2,12 L22,12 M12,2 C6,8 6,16 12,22 C18,16 18,8 12,2", "", ""),
        new("Backends", "Choose how your engines run", "Manage CPU, Vulkan, CUDA and ROCm runtimes and execution settings.", "M5,5 L19,5 L19,19 L5,19 Z M8,2 L8,5 M16,2 L16,5 M8,19 L8,22 M16,19 L16,22 M2,8 L5,8 M19,8 L22,8 M2,16 L5,16 M19,16 L22,16", "", ""),
        new("Terminal", "Terminal", "Live app activity and an interactive PowerShell session.", "M3,5 L21,5 L21,19 L3,19 Z M6,9 L10,12 L6,15 M13,15 L18,15", "", ""),
        new("Benchmark", "Find your best setup", "Choose settings based on measurements from your computer.", "M4,20 L4,12 M12,20 L12,4 M20,20 L20,8", "No benchmark results", "Optimization will measure warmed inference runs and retain the results."),
        new("Diagnostics", "Know your workstation", "Hardware, runtime versions and local storage health.", "M2,12 L6,12 L9,4 L14,20 L18,12 L22,12", "Hardware diagnostics", "No hardware or inference backend is assumed to be available."),
        new("Settings", "Settings", "Appearance and preferences for your local workspace.", "M12,2 L12,6 M12,18 L12,22 M2,12 L6,12 M18,12 L22,12 M5,5 L8,8 M16,16 L19,19 M5,19 L8,16 M16,8 L19,5 M12,7 A5,5 0 1 1 11.99,7", "", "")
    ];
    public IReadOnlyList<string> Themes { get; } = ["System", "Light", "Dark"];
    public IReadOnlyList<string> Densities { get; } = ["Comfortable", "Compact"];
    [ObservableProperty] private NavigationItem? _selectedPage;
    [ObservableProperty] private string _selectedTheme = "System";
    [ObservableProperty] private string _selectedDensity = "Comfortable";
    [ObservableProperty] private string _status = "Workspace ready · no active job";
    private bool _initialized;
    public bool IsSettings => SelectedPage?.IsSettings == true;
    public bool IsNewPage => SelectedPage?.Name == "New Transcription";
    public bool IsJobsPage => SelectedPage?.Name is "Projects" or "Queue";
    public bool IsDiagnosticsPage => SelectedPage?.Name == "Diagnostics";
    public bool IsReviewPage => SelectedPage?.Name == "Review";
    public bool IsModelsPage => SelectedPage?.Name == "Models";
    public bool IsBenchmarkPage => SelectedPage?.Name == "Benchmark";
    public bool IsEmptyPage => !IsSettings && !IsNewPage && !IsJobsPage && !IsDiagnosticsPage && !IsReviewPage && !IsModelsPage && !IsBenchmarkPage && !IsLanguagesPage && !IsBackendsPage && !IsTerminalPage;
    [ObservableProperty] private string _diagnostics = "Detecting hardware…";
    [ObservableProperty] private bool _hardwareChanged;
    public HardwareProfile? Hardware { get; private set; }
    public ObservableCollection<TranscriptionJob> Jobs { get; } = [];
    [ObservableProperty] private string _sourcePath = "";
    [ObservableProperty] private string _selectedLanguage = "auto";
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private bool _hasTranscriptionProgress;
    [ObservableProperty] private bool _isTranscriptionRunning;
    [ObservableProperty] private double _transcriptionPercent;
    [ObservableProperty] private string _transcriptionStage = "Preparing transcription";
    [ObservableProperty] private string _transcriptionProgressSummary = "0% · 0/7 stages finished";
    private Guid? _progressJob;
    [ObservableProperty] private TranscriptionJob? _selectedJob;
    public IReadOnlyList<LanguageOption> Languages { get; } = LanguageCatalog.All.Prepend(new LanguageOption("auto", "Auto-detect language")).ToArray();
    /// <summary>The files a transcription needs. The correction model and its runtime are optional (Settings, off by default).</summary>
    public string Readiness => string.Join("\n", new[] {
        ("Audio", runtimes.Ffmpeg, false), (System.IO.Path.GetFileName(runtimes.WhisperModel), runtimes.WhisperModel, false), (System.IO.Path.GetFileName(runtimes.CanaryModel), runtimes.CanaryModel, false),
        (System.IO.Path.GetFileName(runtimes.CorrectionModel), runtimes.CorrectionModel, true), ("Whisper runtime", runtimes.Whisper, false),
        ("Canary worker", runtimes.CanaryWorker, false), ("Correction runtime", runtimes.LlamaServer, true) }
        .Select(item => $"{item.Item1} · {(System.IO.File.Exists(item.Item2) ? "Ready" : item.Item3 ? "Not installed (optional: only used if the correction model is turned on in Settings)" : "Missing — open Models")}"));
    public ObservableCollection<ReviewRegion> Regions { get; } = [];
    public System.ComponentModel.ICollectionView ReviewItems { get; private set; } = null!;
    [ObservableProperty] private ReviewRegion? _selectedRegion;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _uncertainOnly;
    [ObservableProperty] private string _reviewSummary = "Open a completed project to review its transcript.";
    [ObservableProperty] private Uri? _audioSource;
    [ObservableProperty] private string _normalizedAudioPath = "";
    [ObservableProperty] private string _rawWhisper = "";
    [ObservableProperty] private string _rawCanary = "";
    public IReadOnlyList<string> ExportModes { get; } = ["Strict Verbatim", "Readable"];
    [ObservableProperty] private string _selectedExportMode = "Strict Verbatim";
    [ObservableProperty] private string _engineStatus = "Hardware detection pending";
    private FinalTranscript? _review;
    private CancellationTokenSource? _jobCancellation;
    public string ThemeSummary => SelectedTheme == "System" ? "Following Windows appearance" : $"{SelectedTheme} appearance";
    public string StorageSummary => $"Projects and logs: {storage.Root}\nModels: {runtimes.ModelRoot}";
    public void SetStorageLocation(string path, bool forModels)
    {
        if (IsProcessing || IsModelBusy || IsBenchmarking) { Status = "Finish the active operation before changing folders."; return; }
        try
        {
            var location = StorageLocations.Load();
            StorageLocations.Save(forModels ? location with { ModelRoot = System.IO.Path.GetFullPath(path) } : location with { DataRoot = System.IO.Path.GetFullPath(path), ModelRoot = location.ModelRoot ?? runtimes.ModelRoot });
            Status = "Folder saved. Restart Mockingbird Studio to apply it. Existing files stay in their current folder.";
        }
        catch (Exception error) { ReportError("Cannot save folder", error.Message); }
    }
    public double ContentSpacing => SelectedDensity == "Compact" ? 24 : 36;
    public System.Windows.Thickness ContentMargin => new(ContentSpacing, ContentSpacing, ContentSpacing, 28);

    public async Task InitializeAsync()
    {
        InitializeTerminal();
        var settings = await store.LoadAsync();
        SelectedTheme = settings.Theme;
        SelectedDensity = settings.Density;
        AnimateErrors = settings.AnimateErrors;
        RestoreUpdateSettings(settings);
        RestoreResourceSettings(settings);
        RestoreSpeechDetectionSettings(settings);
        if (store.LastLoadError is not null) ReportError("Preferences could not be restored", "Defaults were loaded. " + store.LastLoadError);
        if (runtimes.StorageLoadError is not null) ReportError("Saved folders could not be restored", "Existing model files have not been removed. Select your previous model repository in Settings. " + runtimes.StorageLoadError);
        SelectedPage = Navigation[0];
        themes.Apply(SelectedTheme);
        await repository.InitializeAsync();
        foreach (var saved in await repository.ListAsync())
        {
            var job = saved;
            if (job.State is not (JobState.Complete or JobState.Failed or JobState.Cancelled or JobState.Queued))
            { job = job with { State = JobState.Cancelled, Error = "Previous run was interrupted. Resume to reuse completed stages." }; await repository.SaveAsync(job); }
            Jobs.Add(job);
        }
        await InitializeModelsAsync();
        await LoadLanguageExpansionsAsync();
        await LoadBenchmarkAsync();
        ReviewItems = System.Windows.Data.CollectionViewSource.GetDefaultView(Regions);
        ReviewItems.Filter = item => item is ReviewRegion region && (!UncertainOnly || region.IsUncertain)
            && (string.IsNullOrWhiteSpace(SearchText) || region.Text.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
            || region.Whisper.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) || region.Canary.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase));
        OnPropertyChanged(nameof(ReviewItems));
        void UpdateJob(object? sender, TranscriptionJob job) => System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var existing = Jobs.FirstOrDefault(item => item.Id == job.Id);
            var index = existing is null ? -1 : Jobs.IndexOf(existing);
            if (index >= 0) Jobs[index] = job; else Jobs.Insert(0, job);
            SelectedJob = job;
            Status = job.Error ?? job.State.ToString();
            activity.Append("job", $"{job.Id:N} · {job.State}" + (job.Error is null ? "" : " · " + job.Error));
            if (job.Error is not null) ReportError("Transcription needs attention", job.Error);
        });
        queue.JobChanged += UpdateJob;
        pipeline.JobChanged += UpdateJob;
        pipeline.ProgressChanged += (_, update) => System.Windows.Application.Current.Dispatcher.Invoke(() => ApplyTranscriptionProgress(update));
        stages.IssueOccurred += (_, issue) => System.Windows.Application.Current.Dispatcher.Invoke(() => ReportError(issue.Title, issue.Message));
        await ReadUpdateResultAsync();
        _initialized = true;
    }
    public void ApplyTranscriptionProgress(TranscriptionProgress update)
    {
        if (_progressJob != update.JobId || update.Percent == 0 && update.CompletedStages == 0 && update.Stage == "Preparing audio")
        { _progressJob = update.JobId; TranscriptionPercent = 0; }
        HasTranscriptionProgress = true;
        IsTranscriptionRunning = update.IsRunning;
        TranscriptionPercent = Math.Max(TranscriptionPercent, update.Percent);
        TranscriptionStage = update.Stage;
        TranscriptionProgressSummary = $"{TranscriptionPercent:0}% · {update.CompletedStages}/{update.TotalStages} stages finished";
    }

    partial void OnSelectedPageChanged(NavigationItem? value)
    {
        OnPropertyChanged(nameof(IsSettings));
        OnPropertyChanged(nameof(IsEmptyPage));
        OnPropertyChanged(nameof(IsNewPage));
        OnPropertyChanged(nameof(IsJobsPage));
        OnPropertyChanged(nameof(IsDiagnosticsPage));
        OnPropertyChanged(nameof(IsReviewPage));
        OnPropertyChanged(nameof(IsModelsPage));
        OnPropertyChanged(nameof(IsBenchmarkPage));
        OnPropertyChanged(nameof(IsLanguagesPage)); OnPropertyChanged(nameof(IsBackendsPage));
        OnPropertyChanged(nameof(IsTerminalPage)); RefreshTerminal();
        if (value?.IsSettings == true) RefreshResourceSummary(); // the computer may have been plugged in or unplugged since the summary was built
    }
    partial void OnSelectedThemeChanged(string value)
    {
        OnPropertyChanged(nameof(ThemeSummary));
        if (_initialized) { themes.Apply(value); Persist(); }
    }
    partial void OnSelectedDensityChanged(string value)
    {
        OnPropertyChanged(nameof(ContentSpacing));
        OnPropertyChanged(nameof(ContentMargin));
        if (_initialized) Persist();
    }
    private async void Persist()
    {
        try { var settings = CurrentSettings(); await store.SaveAsync(settings); await records.SaveAsync(new("settings", "appearance", JsonSerializer.Serialize(settings))); Status = "Preferences saved locally"; }
        catch (Exception error)
        {
            ReportError("Preferences could not be saved", "Check access to the data folder.");
            logger.LogWarning("Preference save failed: {ErrorType}", error.GetType().Name);
        }
    }
    [RelayCommand]
    private async Task PrepareAudioAsync()
    {
        if (IsProcessing || IsBenchmarking || IsModelBusy) return;
        if (!System.IO.File.Exists(SourcePath)) { ReportError("No recording selected", "Choose an existing audio or video file first."); return; }
        var missing = MissingRequiredModels();
        if (missing.Length > 0)
        {
            ReportError("Selected models are not downloaded", string.Join("\n", missing) +
                "\nPress Download selected models, or choose Balanced to reuse the installed models. Selecting a preset does not download its models.");
            return;
        }
        IsProcessing = true;
        _jobCancellation = new();
        try
        {
            using var awake = SleepGuard.Begin("Mockingbird Studio is transcribing");
            var job = await queue.EnqueueAsync(SourcePath, SelectedLanguage, _jobCancellation.Token);
            job = await pipeline.RunAsync(job, _jobCancellation.Token);
            if (job.State == JobState.Complete) await OpenReviewAsync();
        }
        catch (OperationCanceledException) { Status = "Operation cancelled."; }
        catch (Exception error) { ReportError("Operation failed", error.Message); }
        finally { _jobCancellation.Dispose(); _jobCancellation = null; IsProcessing = false; }
    }
    [RelayCommand]
    private async Task ResumeAudioAsync()
    {
        if (SelectedJob is null || IsProcessing || IsBenchmarking || IsModelBusy) return;
        IsProcessing = true;
        _jobCancellation = new();
        try { using var awake = SleepGuard.Begin("Mockingbird Studio is transcribing"); var job = await pipeline.RunAsync(SelectedJob, _jobCancellation.Token); if (job.State == JobState.Complete) await OpenReviewAsync(); }
        catch (OperationCanceledException) { Status = "Operation cancelled."; }
        catch (Exception error) { ReportError("Operation failed", error.Message); }
        finally { _jobCancellation.Dispose(); _jobCancellation = null; IsProcessing = false; }
    }
    [RelayCommand] private void Cancel() => _jobCancellation?.Cancel();
    [RelayCommand]
    private async Task DetectHardwareAsync()
    {
        if (IsCheckingSystem || IsProcessing || IsModelBusy || IsBenchmarking) return;
        IsCheckingSystem = true;
        try
        {
            var profile = await hardware.DetectAsync(storage.Root, runtimes.Whisper);
            Hardware = profile;
            UpdateRecommendation();
            EngineStatus = $"{profile.Topology.LogicalProcessors} CPU threads · {profile.Gpus.FirstOrDefault()?.Name ?? "CPU only"}";
            var path = System.IO.Path.Combine(storage.Root, "Config", "hardware-profile.json");
            if (System.IO.File.Exists(path))
            {
                var old = JsonSerializer.Deserialize<HardwareProfile>(await System.IO.File.ReadAllTextAsync(path));
                HardwareChanged = old?.Fingerprint != profile.Fingerprint;
            }
            Diagnostics = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
            var executionPath = System.IO.Path.Combine(storage.Root, "Config", "execution-profile.json");
            if (System.IO.File.Exists(executionPath))
            {
                var measured = JsonSerializer.Deserialize<TriAsr.Benchmark.ExecutionProfile>(await System.IO.File.ReadAllTextAsync(executionPath));
                HardwareChanged |= measured?.Fingerprint != runtimes.ConfigurationFingerprint(profile.Fingerprint);
            }
            await System.IO.File.WriteAllTextAsync(path, Diagnostics);
            await RefreshBackendsAsync();
            if (HardwareChanged) Status = "Hardware configuration changed. Performance optimization should be rerun.";
        }
        catch (Exception error) { Hardware = null; SystemSummary = "System check failed. Retry before downloading or tuning."; Diagnostics = "Hardware detection failed: " + error.Message; ReportError("System check failed", error.Message); logger.LogWarning(error, "Hardware probe failed: {ErrorType}", error.GetType().Name); }
        finally { IsCheckingSystem = false; }
    }
    [RelayCommand] private void CopyDiagnostics()
    {
        try { System.Windows.Clipboard.SetText(Diagnostics); }
        catch (Exception error) { ReportError("Could not copy diagnostics", error.Message); }
    }
    partial void OnSearchTextChanged(string value) => ReviewItems?.Refresh();
    partial void OnUncertainOnlyChanged(bool value) => ReviewItems?.Refresh();
    [RelayCommand]
    private async Task OpenReviewAsync()
    {
        if (SelectedJob is null) { ReportError("No project selected", "Select a completed project first."); return; }
        try
        {
            _review = TriAsr.Fusion.TranscriptQuality.FlagRepetition(await stages.LoadReviewAsync(SelectedJob.Id));
            var machine = await stages.LoadFinalAsync(SelectedJob.Id);
            Regions.Clear(); for (var i = 0; i < _review.Regions.Count; i++) Regions.Add(new(_review.Regions[i], machine.Regions[i].FinalText));
            SelectedRegion = Regions.FirstOrDefault();
            NormalizedAudioPath = System.IO.Path.Combine(workspace.DirectoryFor(SelectedJob.Id), "normalized.wav");
            var listeningCopy = System.IO.Path.Combine(workspace.DirectoryFor(SelectedJob.Id), "playback.m4a");
            AudioSource = new Uri(System.IO.File.Exists(listeningCopy) ? listeningCopy : NormalizedAudioPath);
            ReviewSummary = $"{_review.Language.ToUpperInvariant()} · {Regions.Count} regions · {Regions.Count(region => region.IsUncertain)} need listening";
            var loopRegions = Regions.Where(region => region.Original.Warnings?.Contains(TriAsr.Fusion.TranscriptQuality.RepetitionWarning) == true).ToArray();
            if (loopRegions.Length > 0)
                ReportError("Possible transcription repetition loop", $"{loopRegions.Length} regions contain a long consecutive repeating pattern. Use Needs listening and play those regions before exporting. Repeated text is preserved because it may be genuinely sung or spoken.");
            SelectedPage = Navigation.First(item => item.Name == "Review");
            var whisperPath = System.IO.Path.Combine(workspace.DirectoryFor(SelectedJob.Id), "whisper.json");
            var whisper = System.IO.File.Exists(whisperPath) ? JsonSerializer.Deserialize<EngineTranscript>(await System.IO.File.ReadAllTextAsync(whisperPath)) : null;
            RawWhisper = whisper?.Text ?? "";
            var canaryPath = System.IO.Path.Combine(workspace.DirectoryFor(SelectedJob.Id), "canary.json");
            RawCanary = System.IO.File.Exists(canaryPath)
                ? JsonSerializer.Deserialize<TriAsr.Engine.Canary.CanaryNative.Result>(await System.IO.File.ReadAllTextAsync(canaryPath))?.Transcript.Text ?? ""
                : System.IO.File.Exists(System.IO.Path.Combine(workspace.DirectoryFor(SelectedJob.Id), "Canary", "skipped-language.json"))
                    ? "This language is outside Canary's coverage. Whisper timestamps and text are preserved; all regions require listening."
                    : "Canary did not complete. All regions require listening.";
            EngineStatus = whisper is null ? "Canary only · no native timestamps · listening required" : $"Whisper · {whisper.ActualBackend} · {whisper.Device}";
        }
        catch (Exception error) { ReportError("Cannot open transcript", error.Message); }
    }
    public FinalTranscript? CurrentTranscript => _review is null ? null : _review with { Regions = Regions.Select(region => region.Snapshot()).ToArray() };
    [RelayCommand] private void UseWhisper() { if (SelectedRegion is not null) SelectedRegion.Text = SelectedRegion.Whisper; }
    [RelayCommand] private void UseCanary() { if (SelectedRegion is not null) SelectedRegion.Text = SelectedRegion.Canary; }
    [RelayCommand] private void UseAi() { if (SelectedRegion is not null) SelectedRegion.Text = SelectedRegion.MachineText; }
    public void MoveReview(int direction)
    {
        var visible = ReviewItems.Cast<ReviewRegion>().Where(region => region.IsUncertain || region.Source == "llm-arbitrated").ToArray();
        if (visible.Length == 0) return;
        var index = Array.IndexOf(visible, SelectedRegion);
        SelectedRegion = visible[Math.Clamp(index + direction, 0, visible.Length - 1)];
    }
    [RelayCommand]
    private async Task SaveReviewAsync()
    {
        if (CurrentTranscript is not { } transcript) return;
        try { await stages.SaveManualAsync(transcript); foreach (var region in Regions) region.AcceptSaved(); Status = "Edits saved with revision history"; ReviewItems.Refresh(); }
        catch (Exception error) { ReportError("Save failed", error.Message); }
    }
    public async Task ExportAsync(string path)
    {
        if (CurrentTranscript is not { } transcript) { ReportError("No transcript open", "Open a transcript before exporting."); return; }
        try
        {
            var exported = SelectedExportMode == "Readable" ? TriAsr.Export.TranscriptExporter.ReadableCopy(transcript) : transcript;
            await TriAsr.Export.TranscriptExporter.SaveAsync(exported, path);
            await records.SaveAsync(new("exports", Guid.NewGuid().ToString("N"), JsonSerializer.Serialize(new { Path = path, Mode = SelectedExportMode, AtUtc = DateTimeOffset.UtcNow }), transcript.JobId));
            Status = "Export saved: " + path;
        }
        catch (Exception error) { ReportError("Export failed", error.Message); }
    }
}
