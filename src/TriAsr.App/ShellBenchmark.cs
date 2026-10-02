using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TriAsr.Benchmark;

namespace TriAsr.App;
public sealed partial class ShellViewModel
{
    public ObservableCollection<BenchmarkRow> BenchmarkResults { get; } = [];
    [ObservableProperty] private string _benchmarkProgress = "";
    [ObservableProperty] private string _benchmarkSummary = "No measured settings yet.";
    [ObservableProperty] private bool _isBenchmarking;
    private ExecutionProfile? _measuredProfile;
    private CancellationTokenSource? _benchmarkCancellation;
    public bool CanApplyBestSettings => _measuredProfile is not null && Hardware is not null && !IsBenchmarking && !IsProcessing && !IsModelBusy
        && _measuredProfile.Fingerprint == runtimes.ConfigurationFingerprint(Hardware.Fingerprint);
    [RelayCommand(CanExecute = nameof(CanApplyBestSettings))]
    private async Task ApplyBestSettingsAsync()
    {
        if (_measuredProfile is null || !CanApplyBestSettings) return;
        try
        {
            var profile = _measuredProfile;
            var settings = new ExecutionSettings(profile.WhisperBackend, profile.WhisperThreads, profile.CanaryBackend, profile.CanaryThreads,
                profile.CorrectionBackend, profile.CorrectionThreads, profile.ParallelSpeech, profile.Fingerprint);
            ValidateExecutionSettings(settings);
            await ExecutionSettingsStore.SaveAsync(storage.Root, settings);
            await RefreshBackendsAsync();
            Status = "All best measured settings applied and saved for new jobs.";
        }
        catch (Exception error) { ReportError("Could not apply best settings", error.Message); }
    }
    partial void OnIsBenchmarkingChanged(bool value) { OnPropertyChanged(nameof(CanApplyBestSettings)); ApplyBestSettingsCommand.NotifyCanExecuteChanged(); }
    partial void OnIsModelBusyChanged(bool value) { OnPropertyChanged(nameof(CanApplyBestSettings)); ApplyBestSettingsCommand.NotifyCanExecuteChanged(); }
    partial void OnIsProcessingChanged(bool value) { OnPropertyChanged(nameof(CanApplyBestSettings)); ApplyBestSettingsCommand.NotifyCanExecuteChanged(); }
    [RelayCommand] private void GoBenchmark() => SelectedPage = Navigation.First(page => page.Name == "Benchmark");
    private void RefreshBenchmarkApplicability()
    {
        OnPropertyChanged(nameof(CanApplyBestSettings)); ApplyBestSettingsCommand.NotifyCanExecuteChanged();
        if (_measuredProfile is null) return;
        if (Hardware is null) { BenchmarkSummary = "Saved results found. Checking whether they match this computer and the selected models…"; return; }
        var matches = _measuredProfile.Fingerprint == runtimes.ConfigurationFingerprint(Hardware.Fingerprint);
        HardwareChanged = !matches;
        var applied = _activeExecution == new ExecutionSettings(_measuredProfile.WhisperBackend, _measuredProfile.WhisperThreads, _measuredProfile.CanaryBackend, _measuredProfile.CanaryThreads,
            _measuredProfile.CorrectionBackend, _measuredProfile.CorrectionThreads, _measuredProfile.ParallelSpeech, _measuredProfile.Fingerprint);
        BenchmarkSummary = (matches ? applied ? "Active · new transcriptions use these measured settings." : "Best settings ready · press Apply all best settings to use them." : "Out of date · rerun tuning for this hardware, app and selected models. Your current valid settings or safe defaults remain in use.") +
            $"\nWhisper: {_measuredProfile.WhisperBackend.ToUpperInvariant()}, {_measuredProfile.WhisperThreads} threads · Canary: {_measuredProfile.CanaryBackend.ToUpperInvariant()}, {_measuredProfile.CanaryThreads} threads · {(_measuredProfile.ParallelSpeech ? "both speech engines run together" : "speech engines run one after the other")}" +
            $"\nMeasured {_measuredProfile.MeasuredUtc.LocalDateTime:g}. Lower times are faster; speed rankings do not establish word accuracy.";
        var combined = _measuredProfile.Results.FirstOrDefault(row => row.Engine == "Dual ASR" && row.Strategy == (_measuredProfile.ParallelSpeech ? "parallel" : "sequential") && row.Error is null);
        if (combined is not null) BenchmarkSummary += "\nSpeech pass: " + combined.Meaning + ". Short-sample timing includes model loading and excludes language detection, correction and export.";
    }
    [RelayCommand]
    private async Task OptimizeAsync()
    {
        if (SetupBusy()) return;
        await DetectHardwareAsync();
        if (Hardware is null) return;
        var missing = ModelCards.Where(card => card.Selected && !File.Exists(card.Location)).Select(card => card.Title).ToArray();
        if (missing.Length > 0)
        {
            ReportError("Models are not ready", "Download the recommended models, or install your selected models on Models, before tuning.\nMissing: " + string.Join(", ", missing));
            BenchmarkProgress = "Step 2: download models first."; return;
        }
        var source = File.Exists(SourcePath) ? SourcePath : SelectedJob?.SourcePath;
        if (source is null || !File.Exists(source))
        {
            ReportError("Choose a tuning recording", "Select a recording with at least three seconds of speech. Tuning uses up to eight seconds of it.");
            BenchmarkProgress = "Step 3: choose a speech recording."; return;
        }
        IsBenchmarking = true; _benchmarkCancellation = new(); using var awake = TriAsr.Infrastructure.SleepGuard.Begin("Mockingbird Studio is tuning");
        BenchmarkResults.Clear();
        try
        {
            var updates = new Progress<BenchmarkRow>(row =>
            {
                if (!BenchmarkResults.Contains(row)) BenchmarkResults.Add(row);
                if (row.Error is not null) ReportError("Tuning candidate failed", $"{row.Engine} · {row.BackendLabel} · {row.ThreadLabel} threads: {row.Error}\nThis candidate is excluded; tuning continues with other settings.");
            });
            var profile = await optimizer.OptimizeAsync(source, Hardware, new Progress<string>(text => BenchmarkProgress = text), _benchmarkCancellation.Token, updates);
            BenchmarkResults.Clear(); foreach (var row in profile.Results) BenchmarkResults.Add(row);
            _measuredProfile = profile;
            RefreshBenchmarkApplicability();
            BenchmarkProgress = "Tuning finished. Review the result, then press Apply all best settings to switch backends, thread counts and speech strategy together.";
            Status = "Tuning complete. Measured settings saved locally.";
        }
        catch (OperationCanceledException) { BenchmarkProgress = "Tuning cancelled. Partial results are kept in Benchmarks; the previous saved profile is preserved."; }
        catch (Exception error) { BenchmarkProgress = "Tuning stopped. The previous profile is preserved."; ReportError("Tuning failed", error.Message); }
        finally { _benchmarkCancellation.Dispose(); _benchmarkCancellation = null; IsBenchmarking = false; }
    }
    [RelayCommand] private void CancelBenchmark() => _benchmarkCancellation?.Cancel();
    private async Task LoadBenchmarkAsync()
    {
        var path = Path.Combine(storage.Root, "Config", "tuning-results.json");
        if (!File.Exists(path)) path = Path.Combine(storage.Root, "Config", "execution-profile.json");
        if (!File.Exists(path)) return;
        try
        {
            _measuredProfile = JsonSerializer.Deserialize<ExecutionProfile>(await File.ReadAllTextAsync(path));
            if (_measuredProfile is null) return;
            foreach (var row in _measuredProfile.Results) BenchmarkResults.Add(row);
            RefreshBenchmarkApplicability();
            BenchmarkProgress = "Saved measurements restored. Run tuning again after changing your hardware or models.";
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        { ReportError("Could not restore tuning results", "Run tuning again. " + error.Message); }
    }
}
