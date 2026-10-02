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

/// <summary>One sidebar page. Advanced pages stay hidden until the user opens "Advanced" (or navigates to one).</summary>
public sealed record NavigationItem(string Name, string Title, string Icon, bool Advanced = false)
{
    public bool IsSettings => Name == "Settings";
}

public sealed partial class ShellViewModel(SettingsStore store, ThemeManager themes, ILogger<ShellViewModel> logger,
    IJobRepository repository, AudioJobQueue queue, RuntimePaths runtimes, HardwareProfiler hardware, IStoragePaths storage,
    TranscriptionPipeline pipeline, LocalTranscriptionStages stages, IJobWorkspace workspace, ModelStore models, LocalOptimizer optimizer, IRecordRepository records,
    ActivityFeed activity, InteractiveTerminal terminal, UpdateService updates, ResourceGovernor governor, IProcessRunner processes) : ObservableObject
{
    public IReadOnlyList<NavigationItem> Navigation { get; } =
    [
        new(Loc.Key("New Transcription"), Loc.Key("New transcription"), "M12,3 L12,21 M3,12 L21,12"),
        new(Loc.Key("Projects"), Loc.Key("Projects"), "M3,6 L10,6 L12,8 L21,8 L21,20 L3,20 Z"),
        new(Loc.Key("Review"), Loc.Key("Review"), "M3,12 L9,18 L21,5"),
        new(RemoteServerPage, RemoteServerPage, "M3,5 L21,5 L21,15 L3,15 Z M8,19 L16,19 M12,15 L12,19"),
        new(Loc.Key("Models"), Loc.Key("Models"), "M12,2 L22,7 L22,17 L12,22 L2,17 L2,7 Z M2,7 L12,12 L22,7 M12,12 L12,22"),
        new(Loc.Key("Languages"), Loc.Key("Languages"), "M2,12 A10,10 0 1 0 22,12 A10,10 0 1 0 2,12 M2,12 L22,12 M12,2 C6,8 6,16 12,22 C18,16 18,8 12,2", true),
        new(Loc.Key("Backends"), Loc.Key("Backends"), "M5,5 L19,5 L19,19 L5,19 Z M8,2 L8,5 M16,2 L16,5 M8,19 L8,22 M16,19 L16,22 M2,8 L5,8 M19,8 L22,8 M2,16 L5,16 M19,16 L22,16", true),
        new(Loc.Key("Benchmark"), Loc.Key("Benchmark"), "M4,20 L4,12 M12,20 L12,4 M20,20 L20,8", true),
        new(Loc.Key("Diagnostics"), Loc.Key("Diagnostics"), "M2,12 L6,12 L9,4 L14,20 L18,12 L22,12", true),
        new(Loc.Key("Terminal"), Loc.Key("Terminal"), "M3,5 L21,5 L21,19 L3,19 Z M6,9 L10,12 L6,15 M13,15 L18,15", true),
        new(Loc.Key("Settings"), Loc.Key("Settings"), "M12,2 L12,6 M12,18 L12,22 M2,12 L6,12 M18,12 L22,12 M5,5 L8,8 M16,16 L19,19 M5,19 L8,16 M16,8 L19,5 M12,7 A5,5 0 1 1 11.99,7")
    ];
    /// <summary>Whether the Advanced pages (Languages, Backends, Benchmark, Diagnostics, Terminal) are listed in the sidebar. The open page is always listed.</summary>
    [ObservableProperty] private bool _showAdvanced;
    [RelayCommand] private void ToggleAdvanced() => ShowAdvanced = !ShowAdvanced;
    public IReadOnlyList<string> Themes { get; } = ["System", "Light", "Dark"];
    public IReadOnlyList<string> Densities { get; } = ["Comfortable", "Compact"];
    [ObservableProperty] private NavigationItem? _selectedPage;
    [ObservableProperty] private string _selectedTheme = "System";
    [ObservableProperty] private string _selectedDensity = "Comfortable";
    [ObservableProperty] private string _status = Loc.Key("Workspace ready · no active job");
    private bool _initialized;
    public bool IsSettings => SelectedPage?.IsSettings == true;
    public bool IsNewPage => SelectedPage?.Name == "New Transcription";
    public bool IsJobsPage => SelectedPage?.Name == "Projects";
    public bool IsDiagnosticsPage => SelectedPage?.Name == "Diagnostics";
    public bool IsReviewPage => SelectedPage?.Name == "Review";
    public bool IsRemotePage => SelectedPage?.Name == RemoteServerPage;
    public bool IsModelsPage => SelectedPage?.Name == "Models";
    public bool IsBenchmarkPage => SelectedPage?.Name == "Benchmark";
    [ObservableProperty] private string _diagnostics = Loc.Key("Detecting hardware…");
    [ObservableProperty] private bool _hardwareChanged;
    public HardwareProfile? Hardware { get; private set; }
    public ObservableCollection<TranscriptionJob> Jobs { get; } = [];
    [ObservableProperty] private string _sourcePath = "";
    [ObservableProperty] private string _selectedLanguage = "auto";
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private bool _hasTranscriptionProgress;
    [ObservableProperty] private bool _isTranscriptionRunning;
    [ObservableProperty] private double _transcriptionPercent;
    [ObservableProperty] private string _transcriptionStage = Loc.Key("Preparing transcription");
    [ObservableProperty] private string _transcriptionProgressSummary = "0% · 0/7 stages finished";
    private Guid? _progressJob;
    [ObservableProperty] private TranscriptionJob? _selectedJob;
    private static readonly LanguageOption AutoDetect = new("auto", Loc.Key("Auto-detect language"));
    private IReadOnlyList<LanguageOption>? _languagesInOrder;
    /// <summary>Auto-detect first, then every language in alphabetical order of the names shown in the interface language.</summary>
    public IReadOnlyList<LanguageOption> Languages => _languagesInOrder ??= LanguageText.InOrder(LanguageCatalog.All).Prepend(AutoDetect).ToArray();
    /// <summary>What still stands between the user and a transcription; empty when everything needed is installed. The correction model is listed only while Settings ask for it, and it never blocks a transcription.</summary>
    public string Readiness
    {
        get
        {
            var missing = new List<string>();
            foreach (var (name, path) in new[] { (Loc.Key("Audio converter"), runtimes.Ffmpeg), (Loc.Key("Whisper runtime"), runtimes.Whisper), (Loc.Key("Canary worker"), runtimes.CanaryWorker) })
                if (!System.IO.File.Exists(path)) missing.Add(T("{0} is missing. Reinstall Mockingbird Studio.", T(name)));
            var models = MissingRequiredModels();
            if (models.Length > 0) missing.Add(T("Not downloaded yet: {0}", string.Join(", ", models)));
            // Not needed to start (a job without it marks disagreements for listening), but the Settings choice is on, so say what is missing.
            if (UseCorrectionModel && !System.IO.File.Exists(runtimes.CorrectionModel)) missing.Add(T("The correction model is not downloaded yet; until it is, disagreements between the engines are marked for listening."));
            return string.Join("\n", missing);
        }
    }
    public bool HasReadinessIssues => Readiness.Length > 0;
    /// <summary>The "missing files" card on New transcription; while setup runs, its banner reports the same download, so the card steps aside.</summary>
    public bool ShowReadinessCard => HasReadinessIssues && !SetupRunning;
    private void RefreshReadiness() { OnPropertyChanged(nameof(Readiness)); OnPropertyChanged(nameof(HasReadinessIssues)); OnPropertyChanged(nameof(ShowReadinessCard)); if (_initialized) RefreshSetupOffer(); }
    partial void OnSelectedLanguageChanged(string value) => RefreshReadiness();
    public ObservableCollection<ReviewRegion> Regions { get; } = [];
    public System.ComponentModel.ICollectionView ReviewItems { get; private set; } = null!;
    [ObservableProperty] private ReviewRegion? _selectedRegion;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _uncertainOnly;
    [ObservableProperty] private string _reviewSummary = Loc.Key("Open a completed project to review its transcript.");
    [ObservableProperty] private Uri? _audioSource;
    [ObservableProperty] private string _normalizedAudioPath = "";
    [ObservableProperty] private string _rawWhisper = "";
    [ObservableProperty] private string _rawCanary = "";
    public IReadOnlyList<string> ExportModes { get; } = [Loc.Key("Strict Verbatim"), Loc.Key("Readable")];
    [ObservableProperty] private string _selectedExportMode = "Strict Verbatim";
    [ObservableProperty] private string _engineStatus = Loc.Key("Hardware detection pending");
    private FinalTranscript? _review;
    private CancellationTokenSource? _jobCancellation;
    public string ThemeSummary => SelectedTheme == "System" ? T("Following Windows appearance") : T("{0} appearance", T(SelectedTheme));
    public string StorageSummary => T("Projects and logs: {0}\nModels: {1}", storage.Root, runtimes.ModelRoot);
    public void SetStorageLocation(string path, bool forModels)
    {
        if (IsProcessing || IsModelBusy || IsBenchmarking) { Status = T("Finish the active operation before changing folders."); return; }
        try
        {
            var location = StorageLocations.Load();
            StorageLocations.Save(forModels ? location with { ModelRoot = System.IO.Path.GetFullPath(path) } : location with { DataRoot = System.IO.Path.GetFullPath(path), ModelRoot = location.ModelRoot ?? runtimes.ModelRoot });
            Status = T("Folder saved. Restart Mockingbird Studio to apply it. Existing files stay in their current folder.");
        }
        catch (Exception error) { ReportError(T("Cannot save folder"), error.Message); }
    }
    public double ContentSpacing => SelectedDensity == "Compact" ? 24 : 36;
    public System.Windows.Thickness ContentMargin => new(ContentSpacing, ContentSpacing, ContentSpacing, 28);

    public async Task InitializeAsync()
    {
        InitializeTerminal();
        RegisterTranslatedDefaults();
        var settings = await store.LoadAsync();
        SelectedTheme = settings.Theme;
        SelectedDensity = settings.Density;
        AnimateErrors = settings.AnimateErrors;
        RestoreUpdateSettings(settings);
        RestoreResourceSettings(settings);
        RestoreSpeechDetectionSettings(settings);
        RestoreLanguage(settings);
        RestoreSetupSettings(settings);
        RestoreWatchSettings(settings);
        Host.Restore(settings);
        Servers.Start();
        if (store.LastLoadError is not null) ReportError(T("Preferences could not be restored"), T("Defaults were loaded. {0}", store.LastLoadError));
        if (runtimes.StorageLoadError is not null) ReportError(T("Saved folders could not be restored"), T("Existing model files have not been removed. Select your previous model repository in Settings. {0}", runtimes.StorageLoadError));
        SelectedPage = Navigation[0];
        themes.Apply(SelectedTheme);
        await repository.InitializeAsync();
        foreach (var saved in await repository.ListAsync())
        {
            var job = saved;
            if (job.State is not (JobState.Complete or JobState.Failed or JobState.Cancelled or JobState.Queued))
            { job = job with { State = JobState.Cancelled, Error = Loc.Key("Previous run was interrupted. Resume to reuse completed stages.") }; await repository.SaveAsync(job); }
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
            if ((!IsWatchBusy || IsProcessing) && !IsApiJob(job)) SelectedJob = job; // a watched or uploaded recording must not move the user's selection
            Status = job.Error is { } failure ? Loc.Describe(failure) : JobText.State(job.State);
            activity.Append("job", $"{job.Id:N} · {job.State}" + (job.Error is null ? "" : " · " + job.Error));
            if (job.Error is not null) ReportError(T("Transcription needs attention"), job.Error);
        });
        _updateJob = job => UpdateJob(null, job);
        Host.JobChangedByApi += (_, job) => _updateJob?.Invoke(job);
        queue.JobChanged += UpdateJob;
        pipeline.JobChanged += UpdateJob;
        pipeline.ProgressChanged += (_, update) => System.Windows.Application.Current.Dispatcher.Invoke(() => ApplyTranscriptionProgress(update));
        stages.IssueOccurred += (_, issue) => System.Windows.Application.Current.Dispatcher.Invoke(() => ReportError(issue.Title, issue.Message));
        await ReadUpdateResultAsync();
        RefreshSetupOffer();
        _initialized = true;
        if (WatchEnabled) _ = RestartWatchAsync();
        _ = Host.StartAsync();
    }
    public void ApplyTranscriptionProgress(TranscriptionProgress update)
    {
        if (_progressJob != update.JobId || update.Percent == 0 && update.CompletedStages == 0 && update.Stage == "Preparing audio")
        { _progressJob = update.JobId; TranscriptionPercent = 0; }
        HasTranscriptionProgress = true;
        IsTranscriptionRunning = update.IsRunning;
        TranscriptionPercent = Math.Max(TranscriptionPercent, update.Percent);
        _lastProgress = update;
        ShowTranscriptionProgress();
    }
    private TranscriptionProgress? _lastProgress;
    private void ShowTranscriptionProgress()
    {
        if (_lastProgress is not { } update) return;
        TranscriptionStage = T(update.Stage);
        TranscriptionProgressSummary = T("{0:0}% · {1}/{2} stages finished", TranscriptionPercent, update.CompletedStages, update.TotalStages);
    }

    partial void OnSelectedPageChanged(NavigationItem? value)
    {
        OnPropertyChanged(nameof(IsSettings));
        OnPropertyChanged(nameof(IsNewPage));
        OnPropertyChanged(nameof(IsJobsPage));
        OnPropertyChanged(nameof(IsDiagnosticsPage));
        OnPropertyChanged(nameof(IsReviewPage)); OnPropertyChanged(nameof(IsRemotePage));
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
        try { var settings = CurrentSettings(); await store.SaveAsync(settings); await records.SaveAsync(new("settings", "appearance", JsonSerializer.Serialize(settings))); Status = T("Preferences saved locally"); }
        catch (Exception error)
        {
            ReportError(T("Preferences could not be saved"), T("Check access to the data folder."));
            logger.LogWarning("Preference save failed: {ErrorType}", error.GetType().Name);
        }
    }
    [RelayCommand]
    private async Task PrepareAudioAsync()
    {
        if (IsProcessing || IsBenchmarking || IsModelBusy || SetupRunning) return;
        if (!System.IO.File.Exists(SourcePath)) { ReportError(T("No recording selected"), T("Choose an existing audio or video file first.")); return; }
        var missing = MissingRequiredModels();
        if (missing.Length > 0)
        {
            ReportError(T("Selected models are not downloaded"), T("{0}\nPress Download models first. Choosing a preset does not download its models.", string.Join("\n", missing)));
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
        catch (OperationCanceledException) { Status = T("Operation cancelled."); }
        catch (Exception error) { ReportError(T("Operation failed"), error.Message); }
        finally { _jobCancellation.Dispose(); _jobCancellation = null; IsProcessing = false; }
    }
    [RelayCommand]
    private async Task ResumeAudioAsync()
    {
        if (SelectedJob is null || IsProcessing || IsBenchmarking || IsModelBusy || SetupRunning) return;
        IsProcessing = true;
        _jobCancellation = new();
        try { using var awake = SleepGuard.Begin("Mockingbird Studio is transcribing"); var job = await pipeline.RunAsync(SelectedJob, _jobCancellation.Token); if (job.State == JobState.Complete) await OpenReviewAsync(); }
        catch (OperationCanceledException) { Status = T("Operation cancelled."); }
        catch (Exception error) { ReportError(T("Operation failed"), error.Message); }
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
            ShowHardwareStatus(profile);
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
            if (HardwareChanged) Status = T("Hardware configuration changed. Performance optimization should be rerun.");
        }
        catch (Exception error) { Hardware = null; SystemSummary = T("System check failed. Retry before downloading or tuning."); Diagnostics = T("Hardware detection failed: {0}", Loc.Describe(error.Message)); ReportError(T("System check failed"), error.Message); logger.LogWarning(error, "Hardware probe failed: {ErrorType}", error.GetType().Name); }
        finally { IsCheckingSystem = false; }
    }
    [RelayCommand] private void CopyDiagnostics()
    {
        try { System.Windows.Clipboard.SetText(Diagnostics); }
        catch (Exception error) { ReportError(T("Could not copy diagnostics"), error.Message); }
    }
    partial void OnSearchTextChanged(string value) => ReviewItems?.Refresh();
    partial void OnUncertainOnlyChanged(bool value) => ReviewItems?.Refresh();
    [RelayCommand]
    private async Task OpenReviewAsync()
    {
        if (SelectedJob is null) { ReportError(T("No project selected"), T("Select a completed project first.")); return; }
        try
        {
            _review = TriAsr.Fusion.TranscriptQuality.FlagRepetition(await stages.LoadReviewAsync(SelectedJob.Id));
            var machine = await stages.LoadFinalAsync(SelectedJob.Id);
            Regions.Clear(); for (var i = 0; i < _review.Regions.Count; i++) Regions.Add(new(_review.Regions[i], machine.Regions[i].FinalText));
            SelectedRegion = Regions.FirstOrDefault();
            NormalizedAudioPath = System.IO.Path.Combine(workspace.DirectoryFor(SelectedJob.Id), "normalized.wav");
            var listeningCopy = System.IO.Path.Combine(workspace.DirectoryFor(SelectedJob.Id), "playback.m4a");
            AudioSource = new Uri(System.IO.File.Exists(listeningCopy) ? listeningCopy : NormalizedAudioPath);
            ShowReviewSummary();
            var loopRegions = Regions.Where(region => region.Original.Warnings?.Contains(TriAsr.Fusion.TranscriptQuality.RepetitionWarning) == true).ToArray();
            if (loopRegions.Length > 0)
                ReportError(T("Possible transcription repetition loop"), T("Regions with a long consecutive repeating pattern: {0}. Use Needs listening and play those regions before exporting. Repeated text is preserved because it may be genuinely sung or spoken.", loopRegions.Length));
            SelectedPage = Navigation.First(item => item.Name == "Review");
            var whisperPath = System.IO.Path.Combine(workspace.DirectoryFor(SelectedJob.Id), "whisper.json");
            var whisper = System.IO.File.Exists(whisperPath) ? JsonSerializer.Deserialize<EngineTranscript>(await System.IO.File.ReadAllTextAsync(whisperPath)) : null;
            RawWhisper = whisper?.Text ?? "";
            var canaryPath = System.IO.Path.Combine(workspace.DirectoryFor(SelectedJob.Id), "canary.json");
            // Without Canary's text the box explains why; the explanation is kept in English and shown again in the new language after a switch.
            _rawCanaryNote = System.IO.File.Exists(canaryPath) ? null
                : System.IO.File.Exists(System.IO.Path.Combine(workspace.DirectoryFor(SelectedJob.Id), "Canary", "skipped-language.json"))
                    ? Loc.Key("This language is outside Canary's coverage. Whisper timestamps and text are preserved; all regions require listening.")
                    : Loc.Key("Canary did not complete. All regions require listening.");
            RawCanary = _rawCanaryNote is { } note ? T(note)
                : JsonSerializer.Deserialize<TriAsr.Engine.Canary.CanaryNative.Result>(await System.IO.File.ReadAllTextAsync(canaryPath))?.Transcript.Text ?? "";
            EngineStatus = whisper is null ? T("Canary only · no native timestamps · listening required") : $"Whisper · {whisper.ActualBackend} · {whisper.Device}";
        }
        catch (Exception error) { ReportError(T("Cannot open transcript"), error.Message); }
    }
    private string? _rawCanaryNote;
    private string _hardwareStatus = "";
    private void ShowHardwareStatus(HardwareProfile profile) =>
        EngineStatus = _hardwareStatus = T("CPU threads: {0} · {1}", profile.Topology.LogicalProcessors, profile.Gpus.FirstOrDefault()?.Name ?? T("CPU only"));
    private void ShowReviewSummary()
    {
        if (_review is null) return;
        ReviewSummary = T("{0} · regions: {1} · to listen to: {2}", _review.Language.ToUpperInvariant(), Regions.Count, Regions.Count(region => region.IsUncertain));
    }
    public FinalTranscript? CurrentTranscript => _review is null ? null : _review with { Regions = Regions.Select(region => region.Snapshot()).ToArray() };
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
    [RelayCommand]
    private async Task SaveReviewAsync()
    {
        if (CurrentTranscript is not { } transcript) return;
        try { await stages.SaveManualAsync(transcript); foreach (var region in Regions) region.AcceptSaved(); Status = T("Edits saved with revision history"); ReviewItems.Refresh(); }
        catch (Exception error) { ReportError(T("Save failed"), error.Message); }
    }
    public async Task ExportAsync(string path)
    {
        if (CurrentTranscript is not { } transcript) { ReportError(T("No transcript open"), T("Open a transcript before exporting.")); return; }
        try
        {
            var exported = SelectedExportMode == "Readable" ? TriAsr.Export.TranscriptExporter.ReadableCopy(transcript) : transcript;
            await TriAsr.Export.TranscriptExporter.SaveAsync(exported, path);
            await records.SaveAsync(new("exports", Guid.NewGuid().ToString("N"), JsonSerializer.Serialize(new { Path = path, Mode = SelectedExportMode, AtUtc = DateTimeOffset.UtcNow }), transcript.JobId));
            Status = T("Export saved: {0}", path);
        }
        catch (Exception error) { ReportError(T("Export failed"), error.Message); }
    }
}
