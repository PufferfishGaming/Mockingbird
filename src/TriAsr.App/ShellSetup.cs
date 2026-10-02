using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TriAsr.Infrastructure;

namespace TriAsr.App;

public enum SetupStage { Hidden, Offer, Running, Done, Problem }

/// <summary>First-run setup: check the computer, download the recommended speech models, measure speed on a short test recording and keep the result.</summary>
public sealed partial class ShellViewModel
{
    private const int SetupSteps = 4;
    private string _setupState = SetupPlan.Pending;
    private CancellationTokenSource? _setupCancellation;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SetupBannerVisible), nameof(SetupRunning), nameof(SetupCanStart), nameof(SetupStartLabel), nameof(SetupDismissLabel), nameof(SetupCanDismiss), nameof(ShowReadinessCard))]
    private SetupStage _setupPhase;
    [ObservableProperty] private string _setupTitle = "Set up Mockingbird Studio";
    [ObservableProperty] private string _setupDetail = "";
    [ObservableProperty] private double _setupPercent;
    [ObservableProperty] private bool _setupIndeterminate = true;
    public bool SetupBannerVisible => SetupPhase != SetupStage.Hidden;
    public bool SetupRunning => SetupPhase == SetupStage.Running;
    public bool SetupCanStart => SetupPhase is SetupStage.Offer or SetupStage.Problem;
    public bool SetupCanDismiss => SetupPhase is SetupStage.Offer or SetupStage.Problem or SetupStage.Done;
    public string SetupStartLabel => SetupPhase == SetupStage.Problem ? "Try again" : "Set up now";
    public string SetupDismissLabel => SetupPhase == SetupStage.Offer ? "Not now" : "Close";

    private void RestoreSetupSettings(AppSettings settings) => _setupState = SetupPlan.Normalize(settings.SetupState);

    /// <summary>The models setup downloads: Whisper and Canary, and the correction model while Settings ask for it.</summary>
    private bool SetupWants(ModelCard card) => card.Selected && (card.Entry.Family != "Correction" || UseCorrectionModel);

    private bool RequiredModelsMissing() => ModelCards.Any(card => SetupWants(card) && !File.Exists(card.Location));

    /// <summary>Offers setup on a fresh install (or while models or speed tuning are still missing) until the user answers.</summary>
    private void RefreshSetupOffer() => EvaluateSetupOffer(RequiredModelsMissing(), _measuredProfile is not null);

    public void EvaluateSetupOffer(bool requiredModelsMissing, bool tuned)
    {
        if (SetupPhase is SetupStage.Running or SetupStage.Problem or SetupStage.Done) return;
        var offer = SetupPlan.ShouldOffer(_setupState, requiredModelsMissing, tuned);
        SetupPhase = offer ? SetupStage.Offer : SetupStage.Hidden;
        if (offer)
        {
            SetupTitle = "Set up Mockingbird Studio";
            SetupDetail = (requiredModelsMissing ? "Downloads the speech models recommended for this computer" + RecommendedSetupDownloadText() + " and measures" : "Measures") +
                " its speed with a short test recording, so transcriptions start with the best settings. Everything stays on this computer.";
        }
    }

    private string RecommendedSetupDownloadText()
    {
        var cards = ModelCards.Where(card => SetupWants(card) && !File.Exists(card.Location)).ToArray();
        return cards.Length == 0 ? "" : $" (about {cards.Sum(card => card.Entry.Bytes) / 1073741824d:0.0} GiB)";
    }

    private void ShowSetupStep(int step, string label, bool indeterminate)
    {
        SetupTitle = $"Setting up · step {step} of {SetupSteps} · {label}";
        SetupIndeterminate = indeterminate;
        if (indeterminate) SetupDetail = "";
        SetupPercent = 0;
    }

    [RelayCommand]
    private async Task RunSetupAsync()
    {
        if (SetupPhase == SetupStage.Running || SetupBusy()) return;
        SetupPhase = SetupStage.Running;
        _setupCancellation = new();
        var token = _setupCancellation.Token;
        using var awake = SleepGuard.Begin("Mockingbird Studio is setting up");
        try
        {
            ShowSetupStep(1, "Checking your computer", true);
            await DetectHardwareAsync();
            if (Hardware is null) throw new InvalidOperationException("The system check did not finish. " + SystemSummary);
            token.ThrowIfCancellationRequested();
            ApplySelection(RecommendedSelection());
            await PersistSelectionAsync();

            var required = ModelCards.Where(SetupWants).ToArray();
            bool Complete(ModelCard card) { var state = models.Inspect(card.Entry); return state.Installed && !state.WrongSize; }
            if (!required.All(Complete))
            {
                ShowSetupStep(2, "Downloading speech models", false);
                using var finished = new CancellationTokenSource();
                var watching = WatchSetupDownloadAsync(required, finished.Token);
                try { await DownloadCardsAsync(required); }
                finally { finished.Cancel(); await watching; }
                token.ThrowIfCancellationRequested();
                if (!required.All(Complete)) throw new InvalidOperationException("The models did not finish downloading. " + ModelProgress);
            }

            ShowSetupStep(3, "Preparing a short test recording", true);
            var sample = await TestSpeech.CreateAsync(processes, Path.Combine(storage.Root, "Setup"), token);
            string? tuningNote = null;
            if (sample is null) tuningNote = "No Windows voice was found for the test recording, so speed tuning was skipped. Safe defaults are used; open Benchmark and choose a speech recording to tune later.";
            else
            {
                ShowSetupStep(4, "Measuring speed", true);
                if (await RunTuningAsync(sample)) await ApplyBestSettingsAsync();
                else if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                else tuningNote = "Speed tuning did not finish" + (_tuningError is null ? "" : ": " + _tuningError) + " Safe defaults are used; you can run it again from Benchmark.";
            }

            _setupState = SetupPlan.Done;
            Persist();
            SetupTitle = "Setup complete";
            SetupDetail = tuningNote ?? "The models are downloaded and the speed settings are saved. You can start transcribing.";
            SetupIndeterminate = false; SetupPercent = 100;
            SetupPhase = SetupStage.Done;
        }
        catch (OperationCanceledException)
        {
            SetupTitle = "Setup paused";
            SetupDetail = "Nothing is lost: downloaded models are kept and a partial download resumes where it stopped.";
            SetupPhase = SetupStage.Problem;
        }
        catch (Exception error)
        {
            SetupTitle = "Setup could not finish";
            SetupDetail = error.Message + " Downloaded models are kept.";
            logger.LogWarning(error, "First-run setup failed: {ErrorType}", error.GetType().Name);
            SetupPhase = SetupStage.Problem;
        }
        finally { _setupCancellation?.Dispose(); _setupCancellation = null; }
    }


    /// <summary>Mirrors the download's progress into the setup banner until the download ends.</summary>
    private async Task WatchSetupDownloadAsync(ModelCard[] cards, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                SetupPercent = 100 * SetupPlan.DownloadFraction(cards.Select(card =>
                {
                    var state = models.Inspect(card.Entry);
                    return (card.Entry.Bytes, state.Installed && !state.WrongSize ? card.Entry.Bytes : state.PartialBytes);
                }));
                SetupDetail = ModelProgress;
            }
        }
        catch (OperationCanceledException) { }
    }

    partial void OnBenchmarkProgressChanged(string value) { if (SetupPhase == SetupStage.Running && IsBenchmarking) SetupDetail = value; }

    [RelayCommand]
    private void CancelSetup()
    {
        _setupCancellation?.Cancel();
        _modelCancellation?.Cancel();
        _benchmarkCancellation?.Cancel();
    }

    [RelayCommand]
    private void DismissSetup()
    {
        if (SetupPhase == SetupStage.Offer) { _setupState = SetupPlan.Skipped; Persist(); }
        SetupPhase = SetupStage.Hidden;
    }
}
