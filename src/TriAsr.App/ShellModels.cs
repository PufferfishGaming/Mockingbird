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
    [ObservableProperty] private string _modelProgress = Loc.Key("Downloads stay in your model folder after closing or updating Mockingbird Studio. Paused downloads can resume.");
    [ObservableProperty] private string _recommendation = Loc.Key("Check your computer to get model recommendations.");
    [ObservableProperty] private string _systemSummary = Loc.Key("Checking your computer…");
    [ObservableProperty] private string _recommendedDownloadSummary = Loc.Key("Waiting for the system check");
    [ObservableProperty] private bool _isModelBusy;
    [ObservableProperty] private bool _isCheckingSystem;
    [ObservableProperty] private double _modelDownloadPercent;
    private CancellationTokenSource? _modelCancellation;
    private sealed record Selection(string Canary, string Correction, string Whisper = "whisper-large-v3");
    public IReadOnlyList<string> PerformancePresets { get; } = [Loc.Key("Fast"), Loc.Key("Balanced"), Loc.Key("Maximum Accuracy"), Loc.Key("Custom")];
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
            { ReportError(T("Could not restore model selection"), T("Balanced defaults restored. {0}", Loc.Describe(error.Message))); }
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
        RefreshReadiness(); RefreshBenchmarkApplicability();
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
        var choice = ModelRecommendation.For(Hardware ?? throw new InvalidOperationException(T("Check your computer first.")));
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
        SystemSummary = T("CPU cores: {0} · threads: {1} · RAM: {2:0.0} GiB", Hardware.Topology.PhysicalCores, Hardware.Topology.LogicalProcessors, Hardware.RamBytes / 1073741824d) + "\n" +
            (gpu is null ? T("No dedicated GPU detected") : T("{0} · {1:0.0} GiB VRAM", gpu.Name, gpu.DedicatedBytes / 1073741824d)) +
            " · " + (Hardware.VulkanDevices.Count > 0 ? T("Vulkan available") : T("CPU execution available"));
        Recommendation = T(choice.Reason);
        if (Hardware.RamBytes < 12UL * 1024 * 1024 * 1024) Recommendation += " " + T("RAM is limited; larger recordings may require more memory.");
        RefreshDownloadSummary(); RefreshBenchmarkApplicability();
    }
    private void RefreshDownloadSummary()
    {
        var recommended = ModelCards.Where(card => card.Recommended).ToArray();
        if (recommended.Length == 0) return;
        var remaining = recommended.Sum(card => { var state = models.Inspect(card.Entry); return state.Installed && !state.WrongSize ? 0 : Math.Max(0, card.Entry.Bytes - state.PartialBytes); });
        var total = recommended.Sum(card => card.Entry.Bytes);
        RecommendedDownloadSummary = T("{0}/3 downloaded · {1:0.00} GiB left to download · {2:0.00} GiB total\nStored in {3} and kept when the app is closed or updated", recommended.Count(card => card.Installed), remaining / 1073741824d, total / 1073741824d, runtimes.ModelRoot);
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
        if (!IsProcessing && !IsModelBusy && !IsBenchmarking && !IsCheckingSystem && !SetupRunning && !IsWatchBusy && !IsApiBusy) return false;
        ReportError(T("Setup is busy"), T("Finish or cancel the current operation before changing models or tuning.")); return true;
    }
    [RelayCommand]
    private async Task ApplyRecommendedAsync()
    {
        if (SetupBusy()) return;
        try
        {
            await DetectHardwareAsync(); if (Hardware is null) return;
            ApplySelection(RecommendedSelection()); await PersistSelectionAsync();
            Status = T("Recommended models selected and saved. Download them, then run tuning.");
        }
        catch (Exception error) { ReportError(T("Could not save recommendations"), error.Message); }
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
            ApplySelection(selection); await PersistSelectionAsync(); Status = T("Model selection saved for the next launch.");
        }
        catch (Exception error) { ReportError(T("Could not select model"), error.Message); }
    }
    [RelayCommand]
    private async Task VerifyModelAsync()
    {
        if (SelectedModel is null || SetupBusy()) return;
        var card = SelectedModel; IsModelBusy = true; _modelCancellation = new(); using var awake = TriAsr.Infrastructure.SleepGuard.Begin("Mockingbird Studio is downloading");
        try
        {
            card.Status = T("Checking SHA256…");
            if (!await models.VerifyAsync(card.Entry, _modelCancellation.Token))
                ReportError(T("Model integrity check failed"), T("{0}: file missing or checksum mismatch. Existing files are preserved; move a damaged file aside before downloading again.", card.Title));
            card.Refresh(models.Inspect(card.Entry));
        }
        catch (OperationCanceledException) { card.Refresh(models.Inspect(card.Entry)); ModelProgress = T("Verification cancelled; local models are retained."); }
        catch (Exception error) { card.Status = T("Verification failed"); ReportError(T("Model verification failed"), error.Message); }
        finally { _modelCancellation.Dispose(); _modelCancellation = null; IsModelBusy = false; RefreshDownloadSummary(); }
    }
    private async Task DownloadCardAsync(ModelCard card, CancellationToken token)
    {
        SelectedModel = card;
        var existing = models.Inspect(card.Entry);
        ModelDownloadPercent = existing.Installed ? 100 : Math.Clamp(existing.PartialBytes / (double)card.Entry.Bytes * 100, 0, 100);
        card.Status = existing.Installed ? T("Checking saved model…") : T("Downloading · can pause and resume");
        ModelProgress = card.Title + " · " + (existing.Installed ? T("using the saved file; no download needed") : T("starting / resuming download"));
        var acceptingProgress = true;
        var progress = new Progress<DownloadProgress>(value =>
        {
            if (!acceptingProgress) return;
            ModelDownloadPercent = card.DownloadPercent = Math.Clamp(value.Received / (double)value.Total * 100, 0, 100);
            ModelProgress = T("{0} · {1:0}% · {2:0} / {3:0} MiB · {4:0.0} MiB/s", card.Title, ModelDownloadPercent, value.Received / 1048576d, value.Total / 1048576d, value.BytesPerSecond / 1048576d);
            if (value.Received == value.Total) card.Status = T("Download received · verifying SHA256…");
        });
        try { await models.DownloadAsync(card.Entry, progress, token); }
        finally { acceptingProgress = false; }
        card.Refresh(models.Inspect(card.Entry)); ModelDownloadPercent = 100;
        RefreshReadiness(); RefreshDownloadSummary();
    }
    [RelayCommand]
    private async Task InstallModelAsync()
    {
        if (SelectedModel is null || SetupBusy()) return;
        await DownloadCardsAsync([SelectedModel]);
    }
    private async Task DownloadCardsAsync(ModelCard[] cards)
    {
        IsModelBusy = true; _modelCancellation = new(); using var awake = TriAsr.Infrastructure.SleepGuard.Begin("Mockingbird Studio is downloading");
        try
        {
            var remaining = cards.Sum(card => { var state = models.Inspect(card.Entry); return state.Installed ? 0 : Math.Max(0, card.Entry.Bytes - state.PartialBytes); });
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(runtimes.ModelRoot))!).AvailableFreeSpace;
            if (remaining > 0 && free < remaining + 128L * 1024 * 1024) throw new IOException(T("Need {0:0.00} GiB plus temporary disk space; only {1:0.00} GiB is free on the models drive.", remaining / 1073741824d, free / 1073741824d));
            foreach (var card in cards) { _modelCancellation.Token.ThrowIfCancellationRequested(); await DownloadCardAsync(card, _modelCancellation.Token); }
            ModelProgress = T("Ready offline. These models and their selected settings will remain after restarting or updating Mockingbird Studio.");
            Status = T("Models ready. You can transcribe or run tuning.");
        }
        catch (OperationCanceledException) { ModelProgress = T("Download paused. Saved models and partial downloads are retained; press Download to resume."); }
        catch (Exception error) { ReportError(T("Model download failed"), error.Message); ModelProgress = T("Download stopped. Saved models are retained. Fix the issue and resume."); }
        finally
        {
            foreach (var card in cards) card.Refresh(models.Inspect(card.Entry));
            _modelCancellation.Dispose(); _modelCancellation = null; IsModelBusy = false;
            RefreshDownloadSummary(); RefreshReadiness();
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
            ReportError(T("Setup is busy"), T("Finish the active operation before changing presets.")); return;
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
        try { await PersistSelectionAsync(); Status = T("Preset saved. Download any missing models on Models."); }
        catch (Exception error) { ReportError(T("Could not save model preset"), error.Message); }
    }
    private string[] MissingRequiredModels() => MissingRequiredModelsFor(SelectedLanguage);
    private string[] MissingRequiredModelsFor(string language) => ModelCards.Where(card => card.Selected &&
        (card.Entry.Family == "Whisper" || TriAsr.Domain.LanguageCatalog.CanaryCodes.Contains(language)))
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
        catch (Exception error) { ReportError(T("Could not prepare recommended models"), error.Message); }
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
        catch (Exception error) { ReportError(T("Could not open model folder"), error.Message); }
    }
}
