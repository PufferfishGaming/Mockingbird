using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TriAsr.App;
public sealed partial class ShellViewModel
{
    private ExecutionSettings? _activeExecution;
    public bool IsBackendsPage => SelectedPage?.Name == "Backends";
    public ObservableCollection<BackendAvailability> BackendStatus { get; } = [];
    public IReadOnlyList<string> BackendEngines { get; } = ["Whisper", "Canary", "Correction"];
    public IReadOnlyList<BackendOption> BackendPackages => BackendRuntimes.Options.Where(option => option.Code is "cuda" or "rocm").ToArray();
    [ObservableProperty] private string _runtimeEngine = "Correction";
    [ObservableProperty] private string _runtimeBackend = "rocm";
    [ObservableProperty] private string _backendProgress = "CPU and Vulkan are bundled. CUDA requires NVIDIA hardware; ROCm requires a compatible AMD GPU and HIP runtime. Imported Canary builds must use ABI 0.2.4.";
    [ObservableProperty] private string _whisperBackendChoice = "cpu";
    [ObservableProperty] private string _canaryBackendChoice = "cpu";
    [ObservableProperty] private string _correctionBackendChoice = "cpu";
    [ObservableProperty] private int _whisperThreadChoice = 4;
    [ObservableProperty] private int _canaryThreadChoice = 4;
    [ObservableProperty] private int _correctionThreadChoice = 4;
    [ObservableProperty] private bool _parallelChoice;
    [ObservableProperty] private string _activeBackendSummary = "Automatic safe defaults · run tuning or save manual settings.";
    public IReadOnlyList<BackendOption> WhisperBackendChoices => Choices("Whisper");
    public IReadOnlyList<BackendOption> CanaryBackendChoices => Choices("Canary");
    public IReadOnlyList<BackendOption> CorrectionBackendChoices => Choices("Correction");
    public IReadOnlyList<int> ThreadChoices => Enumerable.Range(1, Math.Max(1, Hardware?.Topology.LogicalProcessors ?? Environment.ProcessorCount)).ToArray();
    private IReadOnlyList<BackendOption> Choices(string engine) => BackendRuntimes.Options.Where(option => option.Code == "cpu" || Hardware is not null && BackendRuntimes.HardwareFits(Hardware, option.Code) && BackendRuntimes.Installed(runtimes, engine, option.Code)).ToArray();
    private async Task RefreshBackendsAsync()
    {
        BackendStatus.Clear();
        foreach (var engine in BackendEngines)
        foreach (var backend in BackendRuntimes.Options)
        {
            var fits = Hardware is not null && BackendRuntimes.HardwareFits(Hardware, backend.Code);
            var installed = BackendRuntimes.Installed(runtimes, engine, backend.Code);
            BackendStatus.Add(new(engine, backend.Name, !fits ? "No compatible hardware detected" : installed ? "Installed · available for tuning" : BackendRuntimes.DownloadAssets(engine, backend.Code).Count > 0 ? "Download runtime package to test" : "Import a compatible runtime build to test", fits && installed));
        }
        OnPropertyChanged(nameof(WhisperBackendChoices)); OnPropertyChanged(nameof(CanaryBackendChoices)); OnPropertyChanged(nameof(CorrectionBackendChoices));
        OnPropertyChanged(nameof(ThreadChoices));
        try
        {
            var active = await ExecutionSettingsStore.LoadAsync(storage.Root);
            _activeExecution = active;
            if (active is not null && Hardware is not null && active.Fingerprint == runtimes.ConfigurationFingerprint(Hardware.Fingerprint))
            {
                WhisperBackendChoice = active.WhisperBackend; WhisperThreadChoice = active.WhisperThreads;
                CanaryBackendChoice = active.CanaryBackend; CanaryThreadChoice = active.CanaryThreads;
                CorrectionBackendChoice = active.CorrectionBackend; CorrectionThreadChoice = active.CorrectionThreads; ParallelChoice = active.ParallelSpeech;
                ActiveBackendSummary = $"Saved settings: Whisper {active.WhisperBackend}/{active.WhisperThreads} · Canary {active.CanaryBackend}/{active.CanaryThreads} · Correction {active.CorrectionBackend}/{active.CorrectionThreads} · {(active.ParallelSpeech ? "parallel speech" : "sequential speech")}";
            }
            else ActiveBackendSummary = active is null ? "Automatic safe defaults · save manual settings or apply a tuning result." : "Saved settings are stale. Safe defaults will be used until you save or apply matching settings.";
        }
        catch (Exception error) { ReportError("Could not restore execution settings", error.Message); }
        RefreshBenchmarkApplicability();
    }
    [RelayCommand]
    private async Task SaveBackendSettingsAsync()
    {
        if (SetupBusy() || Hardware is null) return;
        try
        {
            var settings = new ExecutionSettings(WhisperBackendChoice, WhisperThreadChoice, CanaryBackendChoice, CanaryThreadChoice, CorrectionBackendChoice, CorrectionThreadChoice, ParallelChoice, runtimes.ConfigurationFingerprint(Hardware.Fingerprint));
            ValidateExecutionSettings(settings);
            await ExecutionSettingsStore.SaveAsync(storage.Root, settings);
            await RefreshBackendsAsync(); Status = "Execution settings saved. New jobs will use them; existing job evidence is unchanged.";
        }
        catch (Exception error) { ReportError("Cannot apply backend settings", error.Message); }
    }
    private void ValidateExecutionSettings(ExecutionSettings settings)
    {
        if (Hardware is null) throw new InvalidOperationException("Check your computer first.");
        foreach (var value in new[] { ("Whisper", settings.WhisperBackend, settings.WhisperThreads), ("Canary", settings.CanaryBackend, settings.CanaryThreads), ("Correction", settings.CorrectionBackend, settings.CorrectionThreads) })
        {
            if (value.Item3 < 1 || value.Item3 > Hardware.Topology.LogicalProcessors) throw new InvalidOperationException($"{value.Item1}: choose 1–{Hardware.Topology.LogicalProcessors} CPU threads.");
            if (!BackendRuntimes.HardwareFits(Hardware, value.Item2) || !BackendRuntimes.Installed(runtimes, value.Item1, value.Item2))
                throw new InvalidOperationException($"{value.Item1}: {value.Item2} needs compatible hardware and an installed runtime.");
        }
        if (settings.ParallelSpeech && settings.WhisperBackend != "cpu" && settings.CanaryBackend != "cpu" &&
            !TriAsr.Hardware.GpuMemoryPlanner.Fits(Hardware.Gpus.MaxBy(gpu => gpu.DedicatedBytes)?.DedicatedBytes ?? 0,
                (ulong)(new FileInfo(runtimes.WhisperModel).Length + new FileInfo(runtimes.CanaryModel).Length), 3UL * 1024 * 1024 * 1024, 0, 2UL * 1024 * 1024 * 1024))
            throw new InvalidOperationException("GPU memory is insufficient for parallel speech models; select sequential execution.");
    }
    [RelayCommand]
    private async Task DownloadBackendAsync()
    {
        if (SetupBusy()) return;
        if (Hardware is null || !BackendRuntimes.HardwareFits(Hardware, RuntimeBackend)) { ReportError("Backend is incompatible", "This backend needs compatible GPU hardware. Check your computer first."); return; }
        IsModelBusy = true; _modelCancellation = new(); using var awake = TriAsr.Infrastructure.SleepGuard.Begin("Mockingbird Studio is downloading");
        var acceptingProgress = true;
        try
        {
            BackendProgress = "Downloading pinned " + RuntimeEngine + " " + RuntimeBackend + " runtime…";
            await BackendRuntimes.InstallArchivesAsync(runtimes, RuntimeEngine, RuntimeBackend, models,
                new Progress<TriAsr.Infrastructure.DownloadProgress>(value => { if (acceptingProgress) BackendProgress = $"Runtime download: {value.Received / 1048576d:0} / {value.Total / 1048576d:0} MiB · SHA256 checked before extraction"; }), _modelCancellation.Token);
            acceptingProgress = false;
            BackendProgress = "Runtime installed and kept after restart. Run tuning to verify actual GPU execution before applying it. ROCm packages may require a matching AMD HIP installation.";
            await RefreshBackendsAsync();
        }
        catch (OperationCanceledException) { BackendProgress = "Paused. Archive downloads can resume; active settings are unchanged."; }
        catch (Exception error) { ReportError("Runtime installation failed", error.Message); }
        finally { acceptingProgress = false; _modelCancellation.Dispose(); _modelCancellation = null; IsModelBusy = false; }
    }
    public async Task ImportBackendAsync(string source)
    {
        if (SetupBusy()) return;
        IsModelBusy = true; _modelCancellation = new(); using var awake = TriAsr.Infrastructure.SleepGuard.Begin("Mockingbird Studio is downloading");
        try
        {
            await BackendRuntimes.ImportAsync(runtimes, RuntimeEngine, RuntimeBackend, source, _modelCancellation.Token);
            BackendProgress = "Runtime imported. Run tuning; actual backend and Canary ABI are checked during execution."; await RefreshBackendsAsync();
        }
        catch (OperationCanceledException) { BackendProgress = "Import cancelled. Active settings are unchanged."; }
        catch (Exception error) { ReportError("Runtime import failed", error.Message); }
        finally { _modelCancellation.Dispose(); _modelCancellation = null; IsModelBusy = false; }
    }
}
