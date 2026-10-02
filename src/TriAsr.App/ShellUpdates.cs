using System.IO;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TriAsr.Infrastructure;

namespace TriAsr.App;

/// <summary>Update checks, the update banner and the one-click install. Checking never installs anything by itself.</summary>
public sealed partial class ShellViewModel
{
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(12);
    private UpdateOffer? _updateOffer;
    private DateTimeOffset? _lastUpdateCheck;
    private string? _skippedUpdateVersion;
    private CancellationTokenSource? _updateCancellation;

    [ObservableProperty] private bool _autoCheckUpdates = true;
    [ObservableProperty] private bool _updateBannerVisible;
    [ObservableProperty] private string _updateTitle = "";
    [ObservableProperty] private string _updateDetail = "";
    [ObservableProperty] private double _updatePercent;
    [ObservableProperty] private string _updateStatusText = Loc.T("You are running {0}. Updates have not been checked yet.", AppInfo.VersionLabel);
    // The update texts are built from a recipe so that they can be built again in another language.
    private Func<string>? _updateStatusMake, _updateDetailMake;
    private void SetUpdateStatus(Func<string> make) { _updateStatusMake = make; UpdateStatusText = make(); }
    private void SetUpdateDetail(Func<string> make) { _updateDetailMake = make; UpdateDetail = make(); }
    private void RefreshUpdateTexts()
    {
        if (_updateStatusMake is { } status) UpdateStatusText = status();
        else UpdateStatusText = T("You are running {0}. Updates have not been checked yet.", AppInfo.VersionLabel);
        if (_updateOffer is { } offer && UpdateBannerVisible && !IsUpdateBusy) ShowUpdateOffer(offer);
        else if (_updateDetailMake is { } detail) UpdateDetail = detail();
    }
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CheckForUpdatesNowCommand))] private bool _isCheckingForUpdates;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand)), NotifyCanExecuteChangedFor(nameof(CheckForUpdatesNowCommand)),
        NotifyCanExecuteChangedFor(nameof(DismissUpdateCommand)), NotifyCanExecuteChangedFor(nameof(SkipUpdateCommand))] private bool _isUpdateBusy;

    /// <summary>Arguments for the app the helper starts after the installer finishes. Normally none.</summary>
    public string? UpdateRelaunchArguments { get; set; }
    public UpdateOffer? AvailableUpdate => _updateOffer;
    public string UpdateDirectory => Edition.UpdateDirectory;
    public string UpdateResultPath => Path.Combine(storage.Root, "Config", "update-result.json");
    /// <summary>Only the installed copy can replace itself; a development or portable copy would install a second one.</summary>
    public bool CanSelfUpdate => updates.Options.IsTestSource || Edition.IsInstalledCopy();

    private AppSettings CurrentSettings() => Host.Write(new AppSettings(SelectedTheme, SelectedDensity, AnimateErrors: AnimateErrors,
        CheckForUpdates: AutoCheckUpdates, SkippedUpdateVersion: _skippedUpdateVersion, LastUpdateCheckUtc: _lastUpdateCheck, ResourceProfile: SelectedResourceProfile,
        SkipNonSpeech: SkipNonSpeech, UseCorrectionModel: UseCorrectionModel, SetupState: _setupState,
        WatchFolder: WatchFolder, WatchEnabled: WatchEnabled, WatchLanguage: WatchLanguage, WatchOutput: WatchOutput,
        Language: LanguageChosen ? Language : ""));

    private void RestoreUpdateSettings(AppSettings settings)
    {
        AutoCheckUpdates = settings.CheckForUpdates;
        _skippedUpdateVersion = settings.SkippedUpdateVersion;
        _lastUpdateCheck = settings.LastUpdateCheckUtc;
        if (_lastUpdateCheck is { } last) SetUpdateStatus(() => T("You are running {0}. Last checked {1:g}.", AppInfo.VersionLabel, last.ToLocalTime()));
        try { UpdateService.RemoveStaleDownloads(UpdateDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { logger.LogDebug("Old update files could not be cleared: {ErrorType}", error.GetType().Name); }
    }

    partial void OnAutoCheckUpdatesChanged(bool value) { if (_initialized) Persist(); }

    private async Task SaveUpdateStateAsync()
    {
        try { await store.SaveAsync(CurrentSettings()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { logger.LogWarning("Update state could not be saved: {ErrorType}", error.GetType().Name); }
    }

    /// <summary>Reports what the helper recorded after the previous run's installer finished.</summary>
    private async Task ReadUpdateResultAsync()
    {
        var path = UpdateResultPath;
        if (!File.Exists(path)) return;
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var version = document.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
            var code = document.RootElement.TryGetProperty("exitCode", out var c) && c.TryGetInt32(out var parsed) ? parsed : -1;
            File.Delete(path);
            if (code is 0 or 3010 && version == AppInfo.Version)
            { SetUpdateStatus(() => T("Updated to {0}.", AppInfo.VersionLabel)); Status = T("Updated to {0}.", AppInfo.VersionLabel); }
            else if (code is 0 or 3010)
                SetUpdateStatus(() => T("The installer finished, but you are running {0} instead of v{1}.", AppInfo.VersionLabel, version));
            else
                ReportError(T("The last update did not finish"), T("The installer returned code {0}, so you are still on {1}. Choose Check for updates now in Settings to try again, or download the installer from the project's GitHub release page.", code, AppInfo.VersionLabel));
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        { logger.LogWarning("Update result could not be read: {ErrorType}", error.GetType().Name); }
    }

    private bool CanCheckNow() => !IsCheckingForUpdates && !IsUpdateBusy;
    [RelayCommand(CanExecute = nameof(CanCheckNow))]
    private Task CheckForUpdatesNowAsync() => RunUpdateCheckAsync(manual: true);

    /// <summary>
    /// Asks the release page whether a newer version exists. The automatic check respects the Settings toggle and
    /// runs at most every twelve hours; the manual check always runs. Failures are quiet unless the user asked.
    /// </summary>
    public async Task RunUpdateCheckAsync(bool manual)
    {
        if (IsCheckingForUpdates || IsUpdateBusy) return;
        if (!manual && (!AutoCheckUpdates || (_lastUpdateCheck is { } last && DateTimeOffset.UtcNow - last < UpdateCheckInterval))) return;
        IsCheckingForUpdates = true;
        if (manual) SetUpdateStatus(() => T("Checking for updates…"));
        try
        {
            var offer = await updates.CheckAsync(AppInfo.Version);
            _lastUpdateCheck = DateTimeOffset.UtcNow;
            if (offer is null)
            {
                _updateOffer = null; UpdateBannerVisible = false;
                var checkedAt = _lastUpdateCheck.Value;
                SetUpdateStatus(() => T("You are running the latest version ({0}). Checked {1:g}.", AppInfo.VersionLabel, checkedAt.ToLocalTime()));
                InstallUpdateCommand.NotifyCanExecuteChanged();
            }
            else
            {
                _updateOffer = offer;
                if (manual) _skippedUpdateVersion = null;
                var hidden = !manual && offer.VersionText == _skippedUpdateVersion;
                SetUpdateStatus(() => hidden ? T("Version {0} is available. You chose to skip it; check manually to see it again.", offer.VersionText) : T("Version {0} is available.", offer.VersionText));
                if (!hidden) ShowUpdateOffer(offer);
                InstallUpdateCommand.NotifyCanExecuteChanged();
            }
            await SaveUpdateStateAsync();
        }
        catch (Exception error)
        {
            logger.LogInformation("Update check failed: {ErrorType}", error.GetType().Name);
            if (manual) { SetUpdateStatus(() => T("Could not check for updates: {0}", FriendlyUpdateError(error))); Status = UpdateStatusText; }
        }
        finally { IsCheckingForUpdates = false; }
    }

    private void ShowUpdateOffer(UpdateOffer offer)
    {
        UpdateTitle = T("Version {0} is available", offer.VersionText);
        var notes = string.IsNullOrWhiteSpace(offer.Notes) ? "" : offer.Notes + "\n";
        UpdateDetail = notes + T("You are running {0}. Download size {1:0} MiB, checked against its SHA256 before it runs.", AppInfo.VersionLabel, offer.Bytes / 1048576d)
            + (CanSelfUpdate ? "" : " " + T("This copy is not the installed app, so it cannot update itself; use the installer from the release page."));
        UpdatePercent = 0;
        UpdateBannerVisible = true;
    }

    private bool CanInstallUpdate() => _updateOffer is not null && !IsUpdateBusy && CanSelfUpdate;
    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task InstallUpdateAsync()
    {
        if (_updateOffer is not { } offer) return;
        if (IsProcessing || IsModelBusy || IsBenchmarking || IsWatchBusy || Host.IsBusy) // Host.IsBusy: a recording another computer sent is being transcribed
        {
            SetUpdateDetail(() => T("Finish the running transcription, download or benchmark first, then choose Update now again."));
            Status = UpdateDetail;
            return;
        }
        IsUpdateBusy = true; UpdatePercent = 0;
        _updateCancellation = new();
        var token = _updateCancellation.Token;
        try
        {
            SetUpdateDetail(() => T("Downloading the update…"));
            var progress = new Progress<DownloadProgress>(value =>
            {
                UpdatePercent = value.Total == 0 ? 0 : value.Received * 100d / value.Total;
                SetUpdateDetail(() => T("Downloading {0:0} / {1:0} MiB · checked against SHA256 before it runs", value.Received / 1048576d, value.Total / 1048576d));
            });
            var setup = await updates.DownloadAsync(offer, UpdateDirectory, progress, token);
            token.ThrowIfCancellationRequested();
            UpdatePercent = 100;
            SetUpdateDetail(() => T("Verified. Mockingbird Studio will now close, install the update and reopen."));
            activity.Append("update", $"Verified installer for {offer.VersionText}; starting the update helper");
            UpdateLauncher.Start(setup, Environment.ProcessPath ?? throw new InvalidOperationException(T("The app location is unknown.")),
                Environment.ProcessId, UpdateResultPath, offer.VersionText, UpdateRelaunchArguments);
            System.Windows.Application.Current.Shutdown();
        }
        catch (OperationCanceledException) { SetUpdateDetail(() => T("Update cancelled. Nothing was installed.")); }
        catch (Exception error)
        {
            logger.LogWarning("Update install failed: {ErrorType}", error.GetType().Name);
            ReportError(T("The update could not be installed"), FriendlyUpdateError(error));
            SetUpdateDetail(() => T("The update did not finish. You are still on {0}.", AppInfo.VersionLabel));
        }
        finally { _updateCancellation?.Dispose(); _updateCancellation = null; IsUpdateBusy = false; }
    }

    [RelayCommand] private void CancelUpdate() => _updateCancellation?.Cancel();
    private bool CanDismissUpdate() => !IsUpdateBusy;
    [RelayCommand(CanExecute = nameof(CanDismissUpdate))] private void DismissUpdate() => UpdateBannerVisible = false;
    [RelayCommand(CanExecute = nameof(CanDismissUpdate))]
    private async Task SkipUpdateAsync()
    {
        if (_updateOffer is { } offer) _skippedUpdateVersion = offer.VersionText;
        UpdateBannerVisible = false;
        await SaveUpdateStateAsync();
    }

    private static string FriendlyUpdateError(Exception error) => error switch
    {
        HttpRequestException => T("GitHub could not be reached. Check your internet connection and try again."),
        OperationCanceledException => T("The update server did not respond in time."),
        _ => Loc.Describe(error.Message)
    };
}
