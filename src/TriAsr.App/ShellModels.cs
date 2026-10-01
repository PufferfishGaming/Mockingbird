using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TriAsr.Infrastructure;

namespace TriAsr.App;
public sealed partial class ShellViewModel
{
    public ObservableCollection<ModelCard> ModelCards { get; } = [];
    [ObservableProperty] private ModelCard? _selectedModel;
    [ObservableProperty] private string _modelProgress = "Downloads stay in your model folder after closing or updating Mockingbird Studio. Paused downloads can resume.";
    [ObservableProperty] private string _recommendation = "Check your computer to get model recommendations.";
    [ObservableProperty] private string _systemSummary = "Checking your computer…";
    [ObservableProperty] private string _recommendedDownloadSummary = "Waiting for the system check";
    [ObservableProperty] private bool _isModelBusy;
    [ObservableProperty] private bool _isCheckingSystem;
    [ObservableProperty] private double _modelDownloadPercent;
    private CancellationTokenSource? _modelCancellation;
    private sealed record Selection(string Canary, string Correction, string Whisper = "whisper-large-v3");
    public IReadOnlyList<string> PerformancePresets { get; } = ["Fast", "Balanced", "Maximum Accuracy", "Custom"];
    [ObservableProperty] private string _selectedPreset = "Balanced";
    private async Task InitializeModelsAsync()
    {
        foreach (var entry in ModelManifest.Entries)
        {
            var card = new ModelCard(entry, models.PathFor(entry));
            card.Refresh(models.Inspect(entry)); ModelCards.Add(card);
        }
        var selection = new Selection("canary-q8", "correction-q6");
        var path = Path.Combine(storage.Root, "Config", "model-selection.json");
        if (File.Exists(path))
        {
            try { selection = JsonSerializer.Deserialize<Selection>(await File.ReadAllTextAsync(path)) ?? selection; }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            { ReportError("Could not restore model selection", "Balanced defaults restored. " + error.Message); }
        }
        ApplySelection(selection);
        SelectedModel = ModelCards.First(item => item.Selected);
        await File.WriteAllTextAsync(Path.Combine(storage.Root, "Config", "model-manifest.json"), JsonSerializer.Serialize(ModelManifest.Entries, new JsonSerializerOptions { WriteIndented = true }));
    }
    private void ApplySelection(Selection selection)
    {
        var canary = ModelCards.FirstOrDefault(item => item.Entry.Id == selection.Canary && item.Entry.Family == "Canary") ?? ModelCards.First(item => item.Entry.Id == "canary-q8");
        var correction = ModelCards.FirstOrDefault(item => item.Entry.Id == selection.Correction && item.Entry.Family == "Correction") ?? ModelCards.First(item => item.Entry.Id == "correction-q6");
        var whisper = ModelCards.FirstOrDefault(item => item.Entry.Id == selection.Whisper && item.Entry.Family == "Whisper") ?? ModelCards.First(item => item.Entry.Id == "whisper-large-v3");
        runtimes.WhisperModel = whisper.Location; runtimes.CanaryModel = canary.Location; runtimes.CorrectionModel = correction.Location;
        foreach (var card in ModelCards) card.Selected = card == whisper || card == canary || card == correction;
        _restoringPreset = true;
        try { SelectedPreset = PresetFor(new(canary.Entry.Id, correction.Entry.Id, whisper.Entry.Id)); }
        finally { _restoringPreset = false; }
        OnPropertyChanged(nameof(Readiness)); RefreshBenchmarkApplicability();
    }
    private static string PresetFor(Selection selection) => selection switch
    {
        { Whisper: "whisper-large-v3-q5", Canary: "canary-q4", Correction: "correction-q4" } => "Fast",
        { Whisper: "whisper-large-v3", Canary: "canary-q8", Correction: "correction-q6" } => "Balanced",
        { Whisper: "whisper-large-v3", Canary: "canary-f16", Correction: "correction-q8" } => "Maximum Accuracy",
        _ => "Custom"
    };
    private Selection RecommendedSelection()
    {
        var choice = ModelRecommendation.For(Hardware ?? throw new InvalidOperationException("Check your computer first."));
        return new(choice.Canary, choice.Correction, choice.Whisper);
    }
    private void UpdateRecommendation()
    {
        if (Hardware is null) return;
        var choice = ModelRecommendation.For(Hardware);
        foreach (var card in ModelCards) card.Recommended = card.Entry.Id == choice.Whisper || card.Entry.Id == choice.Canary || card.Entry.Id == choice.Correction;
        var ordered = ModelCards.OrderByDescending(card => card.Highlighted).ThenBy(card => card.Entry.Family switch { "Whisper" => 0, "Canary" => 1, _ => 2 }).ThenBy(card => card.Entry.Id).ToArray();
        for (var index = 0; index < ordered.Length; index++) ModelCards.Move(ModelCards.IndexOf(ordered[index]), index);
        var gpu = Hardware.Gpus.MaxBy(item => item.DedicatedBytes);
        SystemSummary = $"{Hardware.Topology.PhysicalCores} CPU cores / {Hardware.Topology.LogicalProcessors} threads · {Hardware.RamBytes / 1073741824d:0.0} GiB RAM\n" +
            (gpu is null ? "No dedicated GPU detected" : $"{gpu.Name} · {gpu.DedicatedBytes / 1073741824d:0.0} GiB VRAM") +
            $" · {(Hardware.VulkanDevices.Count > 0 ? "Vulkan available" : "CPU execution available")}";
        Recommendation = choice.Reason;
        if (Hardware.RamBytes < 12UL * 1024 * 1024 * 1024) Recommendation += " RAM is limited; larger recordings may require more memory.";
        RefreshDownloadSummary(); RefreshBenchmarkApplicability();
    }
    private void RefreshDownloadSummary()
    {
        var recommended = ModelCards.Where(card => card.Recommended).ToArray();
        if (recommended.Length == 0) return;
        var remaining = recommended.Sum(card => { var state = models.Inspect(card.Entry); return state.Installed && !state.WrongSize ? 0 : Math.Max(0, card.Entry.Bytes - state.PartialBytes); });
        var total = recommended.Sum(card => card.Entry.Bytes);
        RecommendedDownloadSummary = $"{recommended.Count(card => card.Installed)}/3 downloaded · {remaining / 1073741824d:0.00} GiB left to download · {total / 1073741824d:0.00} GiB total\nSaved permanently in {runtimes.ModelRoot}";
    }
    private async Task PersistSelectionAsync()
    {
        var selected = JsonSerializer.Serialize(new Selection(ModelCards.First(item => item.Selected && item.Entry.Family == "Canary").Entry.Id,
            ModelCards.First(item => item.Selected && item.Entry.Family == "Correction").Entry.Id,
            ModelCards.First(item => item.Selected && item.Entry.Family == "Whisper").Entry.Id));
        var path = Path.Combine(storage.Root, "Config", "model-selection.json");
        await File.WriteAllTextAsync(path + ".tmp", selected); File.Move(path + ".tmp", path, true);
        await records.SaveAsync(new("models", "selected", selected));
    }
    private bool SetupBusy()
    {
        if (!IsProcessing && !IsModelBusy && !IsBenchmarking && !IsCheckingSystem) return false;
        ReportError("Setup is busy", "Finish or cancel the current operation before changing models or tuning."); return true;
    }
    [RelayCommand]
    private async Task ApplyRecommendedAsync()
    {
        if (SetupBusy()) return;
        try
        {
            await DetectHardwareAsync(); if (Hardware is null) return;
            ApplySelection(RecommendedSelection()); await PersistSelectionAsync();
            Status = "Recommended models selected and saved. Download them, then run tuning.";
        }
        catch (Exception error) { ReportError("Could not save recommendations", error.Message); }
    }
    [RelayCommand]
    private async Task SelectModelAsync()
    {
        if (SelectedModel is null || SetupBusy()) return;
        try
        {
            var selection = new Selection(ModelCards.First(item => item.Selected && item.Entry.Family == "Canary").Entry.Id,
                ModelCards.First(item => item.Selected && item.Entry.Family == "Correction").Entry.Id,
                ModelCards.First(item => item.Selected && item.Entry.Family == "Whisper").Entry.Id);
            if (SelectedModel.Entry.Family == "Whisper") selection = selection with { Whisper = SelectedModel.Entry.Id };
            if (SelectedModel.Entry.Family == "Canary") selection = selection with { Canary = SelectedModel.Entry.Id };
            if (SelectedModel.Entry.Family == "Correction") selection = selection with { Correction = SelectedModel.Entry.Id };
            ApplySelection(selection); await PersistSelectionAsync(); Status = "Model selection saved for the next launch.";
        }
        catch (Exception error) { ReportError("Could not select model", error.Message); }
    }
    [RelayCommand]
    private async Task VerifyModelAsync()
    {
        if (SelectedModel is null || SetupBusy()) return;
        var card = SelectedModel; IsModelBusy = true; _modelCancellation = new();
        try
        {
            card.Status = "Checking SHA256…";
            if (!await models.VerifyAsync(card.Entry, _modelCancellation.Token))
                ReportError("Model integrity check failed", card.Title + ": file missing or checksum mismatch. Existing files are preserved; move a damaged file aside before downloading again.");
            card.Refresh(models.Inspect(card.Entry));
        }
        catch (OperationCanceledException) { card.Refresh(models.Inspect(card.Entry)); ModelProgress = "Verification cancelled; local models are retained."; }
        catch (Exception error) { card.Status = "Verification failed"; ReportError("Model verification failed", error.Message); }
        finally { _modelCancellation.Dispose(); _modelCancellation = null; IsModelBusy = false; RefreshDownloadSummary(); }
    }
    private async Task DownloadCardAsync(ModelCard card, CancellationToken token)
    {
        SelectedModel = card;
        var existing = models.Inspect(card.Entry);
        ModelDownloadPercent = existing.Installed ? 100 : Math.Clamp(existing.PartialBytes / (double)card.Entry.Bytes * 100, 0, 100);
        card.Status = existing.Installed ? "Checking saved model…" : "Downloading · can pause and resume";
        ModelProgress = card.Title + (existing.Installed ? " · using the saved file; no download needed" : " · starting / resuming download");
        var acceptingProgress = true;
        var progress = new Progress<DownloadProgress>(value =>
        {
            if (!acceptingProgress) return;
            ModelDownloadPercent = card.DownloadPercent = Math.Clamp(value.Received / (double)value.Total * 100, 0, 100);
            ModelProgress = $"{card.Title} · {ModelDownloadPercent:0}% · {value.Received / 1048576d:0} / {value.Total / 1048576d:0} MiB · {value.BytesPerSecond / 1048576d:0.0} MiB/s";
            if (value.Received == value.Total) card.Status = "Download received · verifying SHA256…";
        });
        try { await models.DownloadAsync(card.Entry, progress, token); }
        finally { acceptingProgress = false; }
        card.Refresh(models.Inspect(card.Entry)); ModelDownloadPercent = 100;
        OnPropertyChanged(nameof(Readiness)); RefreshDownloadSummary();
    }
    [RelayCommand]
    private async Task InstallModelAsync()
    {
        if (SelectedModel is null || SetupBusy()) return;
        await DownloadCardsAsync([SelectedModel]);
    }
    private async Task DownloadCardsAsync(ModelCard[] cards)
    {
        IsModelBusy = true; _modelCancellation = new();
        try
        {
            var remaining = cards.Sum(card => { var state = models.Inspect(card.Entry); return state.Installed ? 0 : Math.Max(0, card.Entry.Bytes - state.PartialBytes); });
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(runtimes.ModelRoot))!).AvailableFreeSpace;
            if (remaining > 0 && free < remaining + 128L * 1024 * 1024) throw new IOException($"Need {remaining / 1073741824d:0.00} GiB plus working space; only {free / 1073741824d:0.00} GiB is free in the model drive.");
            foreach (var card in cards) { _modelCancellation.Token.ThrowIfCancellationRequested(); await DownloadCardAsync(card, _modelCancellation.Token); }
            ModelProgress = "Ready offline. These models and their selected settings will remain after restarting or updating Mockingbird Studio.";
            Status = "Models ready. You can transcribe or run tuning.";
        }
        catch (OperationCanceledException) { ModelProgress = "Download paused. Saved models and partial downloads are retained; press Download to resume."; }
        catch (Exception error) { ReportError("Model download failed", error.Message); ModelProgress = "Download stopped. Saved models are retained. Fix the issue and resume."; }
        finally
        {
            foreach (var card in cards) card.Refresh(models.Inspect(card.Entry));
            _modelCancellation.Dispose(); _modelCancellation = null; IsModelBusy = false;
            RefreshDownloadSummary(); OnPropertyChanged(nameof(Readiness));
        }
    }
    [RelayCommand] private void CancelModel() => _modelCancellation?.Cancel();
    private bool _restoringPreset;
    partial void OnSelectedPresetChanged(string? oldValue, string newValue)
    {
        if (_restoringPreset || !_initialized || newValue == "Custom") return;
        if (IsProcessing || IsModelBusy || IsBenchmarking || IsCheckingSystem)
        {
            _restoringPreset = true;
            try { SelectedPreset = oldValue ?? "Custom"; } finally { _restoringPreset = false; }
            ReportError("Setup is busy", "Finish the active operation before changing presets."); return;
        }
        var selection = newValue switch
        {
            "Fast" => new Selection("canary-q4", "correction-q4", "whisper-large-v3-q5"),
            "Maximum Accuracy" => new Selection("canary-f16", "correction-q8"),
            _ => new Selection("canary-q8", "correction-q6")
        };
        ApplySelection(selection); PersistModelSelection();
    }
    private async void PersistModelSelection()
    {
        try { await PersistSelectionAsync(); Status = "Preset saved. Download any missing models on Models."; }
        catch (Exception error) { ReportError("Could not save model preset", error.Message); }
    }
    private string[] MissingRequiredModels() => ModelCards.Where(card => card.Selected &&
        (card.Entry.Family == "Whisper" || TriAsr.Domain.LanguageCatalog.CanaryCodes.Contains(SelectedLanguage)))
        .Where(card => { var status = models.Inspect(card.Entry); return !status.Installed || status.WrongSize; }).Select(card => card.Title).ToArray();
    [RelayCommand]
    private async Task InstallSelectedModelsAsync()
    {
        if (SetupBusy()) return;
        await DownloadCardsAsync(ModelCards.Where(card => card.Selected).ToArray());
    }
    [RelayCommand]
    private async Task InstallRecommendedAsync()
    {
        if (SetupBusy()) return;
        try
        {
            await DetectHardwareAsync(); if (Hardware is null) return;
            ApplySelection(RecommendedSelection()); await PersistSelectionAsync();
            await DownloadCardsAsync(ModelCards.Where(item => item.Selected).ToArray());
        }
        catch (Exception error) { ReportError("Could not prepare recommended models", error.Message); }
    }
    [RelayCommand]
    private void OpenModelFolder()
    {
        if (SelectedModel is null) return;
        try
        {
            var directory = Path.GetDirectoryName(SelectedModel.Location)!; Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { directory }, UseShellExecute = true });
        }
        catch (Exception error) { ReportError("Could not open model folder", error.Message); }
    }
}
