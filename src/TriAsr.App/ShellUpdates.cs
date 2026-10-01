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
    [ObservableProperty] private string _updateStatusText = $"You are running {AppInfo.VersionLabel}. Updates have not been checked yet.";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CheckForUpdatesNowCommand))] private bool _isCheckingForUpdates;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand)), NotifyCanExecuteChangedFor(nameof(CheckForUpdatesNowCommand)),
        NotifyCanExecuteChangedFor(nameof(DismissUpdateCommand)), NotifyCanExecuteChangedFor(nameof(SkipUpdateCommand))] private bool _isUpdateBusy;

    /// <summary>Arguments for the app the helper starts after the installer finishes. Normally none.</summary>
    public string? UpdateRelaunchArguments { get; set; }
    public UpdateOffer? AvailableUpdate => _updateOffer;
    public string UpdateDirectory => Path.Combine(Path.GetTempPath(), "MockingbirdStudio-Update");
    public string UpdateResultPath => Path.Combine(storage.Root, "Config", "update-result.json");
    /// <summary>Only the installed copy can replace itself; a development or portable copy would install a second one.</summary>
    public bool CanSelfUpdate => updates.Options.IsTestSource || IsInstalledCopy();

    private static bool IsInstalledCopy()
    {
        var path = Environment.ProcessPath;
        if (path is null) return false;
        var installRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "TriASR") + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(installRoot, StringComparison.OrdinalIgnoreCase);
    }

    private AppSettings CurrentSettings() => new(SelectedTheme, SelectedDensity, AnimateErrors: AnimateErrors,
        CheckForUpdates: AutoCheckUpdates, SkippedUpdateVersion: _skippedUpdateVersion, LastUpdateCheckUtc: _lastUpdateCheck, ResourceProfile: SelectedResourceProfile);

    private void RestoreUpdateSettings(AppSettings settings)
    {
        AutoCheckUpdates = settings.CheckForUpdates;
        _skippedUpdateVersion = settings.SkippedUpdateVersion;
        _lastUpdateCheck = settings.LastUpdateCheckUtc;
        if (_lastUpdateCheck is { } last) UpdateStatusText = $"You are running {AppInfo.VersionLabel}. Last checked {last.ToLocalTime():g}.";
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
            { UpdateStatusText = $"Updated to {AppInfo.VersionLabel}."; Status = $"Updated to {AppInfo.VersionLabel}."; }
            else if (code is 0 or 3010)
                UpdateStatusText = $"The installer finished, but you are running {AppInfo.VersionLabel} instead of v{version}.";
            else
                ReportError("The last update did not finish", $"The installer returned code {code}, so you are still on {AppInfo.VersionLabel}. Choose Check for updates in Settings to try again, or download the installer from the project's GitHub release page.");
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
        if (manual) UpdateStatusText = "Checking for updates…";
        try
        {
            var offer = await updates.CheckAsync(AppInfo.Version);
            _lastUpdateCheck = DateTimeOffset.UtcNow;
            if (offer is null)
            {
                _updateOffer = null; UpdateBannerVisible = false;
                UpdateStatusText = $"You are running the latest version ({AppInfo.VersionLabel}). Checked {_lastUpdateCheck.Value.ToLocalTime():g}.";
                InstallUpdateCommand.NotifyCanExecuteChanged();
            }
            else
            {
                _updateOffer = offer;
                if (manual) _skippedUpdateVersion = null;
                var hidden = !manual && offer.VersionText == _skippedUpdateVersion;
                UpdateStatusText = $"Version {offer.VersionText} is available.{(hidden ? " You chose to skip it; check manually to see it again." : "")}";
                if (!hidden) ShowUpdateOffer(offer);
                InstallUpdateCommand.NotifyCanExecuteChanged();
            }
            await SaveUpdateStateAsync();
        }
        catch (Exception error)
        {
            logger.LogInformation("Update check failed: {ErrorType}", error.GetType().Name);
            if (manual) { UpdateStatusText = "Could not check for updates: " + FriendlyUpdateError(error); Status = UpdateStatusText; }
        }
        finally { IsCheckingForUpdates = false; }
    }

    private void ShowUpdateOffer(UpdateOffer offer)
    {
        UpdateTitle = $"Version {offer.VersionText} is available";
        var notes = string.IsNullOrWhiteSpace(offer.Notes) ? "" : offer.Notes + "\n";
        UpdateDetail = $"{notes}You are running {AppInfo.VersionLabel}. Download size {offer.Bytes / 1048576d:0} MB, checked against its SHA256 before it runs."
            + (CanSelfUpdate ? "" : " This copy is not the installed app, so it cannot update itself; use the installer from the release page.");
        UpdatePercent = 0;
        UpdateBannerVisible = true;
    }

    private bool CanInstallUpdate() => _updateOffer is not null && !IsUpdateBusy && CanSelfUpdate;
    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task InstallUpdateAsync()
    {
        if (_updateOffer is not { } offer) return;
        if (IsProcessing || IsModelBusy || IsBenchmarking)
        {
            UpdateDetail = "Finish the running transcription, download or benchmark first, then choose Update now again.";
            Status = UpdateDetail;
            return;
        }
        IsUpdateBusy = true; UpdatePercent = 0;
        _updateCancellation = new();
        var token = _updateCancellation.Token;
        try
        {
            UpdateDetail = "Downloading the update…";
            var progress = new Progress<DownloadProgress>(value =>
            {
                UpdatePercent = value.Total == 0 ? 0 : value.Received * 100d / value.Total;
                UpdateDetail = $"Downloading {value.Received / 1048576d:0} / {value.Total / 1048576d:0} MB · checked against SHA256 before it runs";
            });
            var setup = await updates.DownloadAsync(offer, UpdateDirectory, progress, token);
            token.ThrowIfCancellationRequested();
            UpdatePercent = 100;
            UpdateDetail = "Verified. Mockingbird Studio will now close, install the update and reopen.";
            activity.Append("update", $"Verified installer for {offer.VersionText}; starting the update helper");
            UpdateLauncher.Start(setup, Environment.ProcessPath ?? throw new InvalidOperationException("The app location is unknown."),
                Environment.ProcessId, UpdateResultPath, offer.VersionText, UpdateRelaunchArguments);
            System.Windows.Application.Current.Shutdown();
        }
        catch (OperationCanceledException) { UpdateDetail = "Update cancelled. Nothing was installed."; }
        catch (Exception error)
        {
            logger.LogWarning("Update install failed: {ErrorType}", error.GetType().Name);
            ReportError("The update could not be installed", FriendlyUpdateError(error));
            UpdateDetail = $"The update did not finish. You are still on {AppInfo.VersionLabel}.";
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
        HttpRequestException => "GitHub could not be reached. Check your internet connection and try again.",
        OperationCanceledException => "The update server did not respond in time.",
        _ => error.Message
    };
}
