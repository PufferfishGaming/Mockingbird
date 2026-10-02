using System.IO;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TriAsr.Infrastructure;

namespace TriAsr.App;

/// <summary>
/// The Client edition's update check and one-click install: the same behaviour and the same banner as Studio's (ADR-0002), against the Client's own
/// manifest (<c>latest-client.json</c>). Nothing is installed without the user choosing Update now, and nothing is run before its SHA256 matches.
/// </summary>
public sealed partial class ClientViewModel
{
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(12);
    private UpdateOffer? _updateOffer;
    private DateTimeOffset? _lastUpdateCheck;
    private string? _skippedUpdateVersion;
    private CancellationTokenSource? _updateCancellation;
    private Func<string>? _updateStatusMake, _updateDetailMake;

    [ObservableProperty] private bool _autoCheckUpdates = true;
    [ObservableProperty] private bool _updateBannerVisible;
    [ObservableProperty] private string _updateTitle = "";
    [ObservableProperty] private string _updateDetail = "";
    [ObservableProperty] private double _updatePercent;
    [ObservableProperty] private string _updateStatusText = Loc.T("You are running {0}. Updates have not been checked yet.", AppInfo.VersionLabel);
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CheckForUpdatesNowCommand))] private bool _isCheckingForUpdates;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(InstallUpdateCommand)), NotifyCanExecuteChangedFor(nameof(CheckForUpdatesNowCommand)),
        NotifyCanExecuteChangedFor(nameof(DismissUpdateCommand)), NotifyCanExecuteChangedFor(nameof(SkipUpdateCommand))] private bool _isUpdateBusy;

    public string UpdateResultPath => Path.Combine(_storage.Root, "Config", "update-result.json");

    /// <summary>Only the installed copy can replace itself.</summary>
    public bool CanSelfUpdate => _updates.Options.IsTestSource || Edition.IsInstalledCopy();

    // The update texts are built from a recipe, so that they can be built again in another language.
    private void SetUpdateStatus(Func<string> make) { _updateStatusMake = make; UpdateStatusText = make(); }
    private void SetUpdateDetail(Func<string> make) { _updateDetailMake = make; UpdateDetail = make(); }

    private void RefreshUpdateTexts()
    {
        UpdateStatusText = _updateStatusMake is { } status ? status() : Loc.T("You are running {0}. Updates have not been checked yet.", AppInfo.VersionLabel);
        if (_updateOffer is { } offer && UpdateBannerVisible && !IsUpdateBusy) ShowUpdateOffer(offer);
        else if (_updateDetailMake is { } detail) UpdateDetail = detail();
    }

    partial void OnAutoCheckUpdatesChanged(bool value) { if (_initialized) Persist(); }

    private void RestoreUpdateSettings(AppSettings settings)
    {
        AutoCheckUpdates = settings.CheckForUpdates;
        _skippedUpdateVersion = settings.SkippedUpdateVersion;
        _lastUpdateCheck = settings.LastUpdateCheckUtc;
        if (_lastUpdateCheck is { } last) SetUpdateStatus(() => Loc.T("You are running {0}. Last checked {1:g}.", AppInfo.VersionLabel, last.ToLocalTime()));
        try { UpdateService.RemoveStaleDownloads(Edition.UpdateDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _logger.LogDebug("Old update files could not be cleared: {ErrorType}", error.GetType().Name); }
    }

    /// <summary>Reports what the helper recorded after the previous run's installer finished.</summary>
    private void ReadUpdateResult()
    {
        var path = UpdateResultPath;
        if (!File.Exists(path)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var version = document.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
            var code = document.RootElement.TryGetProperty("exitCode", out var c) && c.TryGetInt32(out var parsed) ? parsed : -1;
            File.Delete(path);
            if (code is 0 or 3010 && version == AppInfo.Version) { SetUpdateStatus(() => Loc.T("Updated to {0}.", AppInfo.VersionLabel)); SetNote("Updated to {0}.", AppInfo.VersionLabel); }
            else if (code is 0 or 3010) SetUpdateStatus(() => Loc.T("The installer finished, but you are running {0} instead of v{1}.", AppInfo.VersionLabel, version));
            else ReportError(Loc.T("The last update did not finish"), Loc.T("The installer returned code {0}, so you are still on {1}. Choose Check for updates now in Settings to try again, or download the installer from the project's GitHub release page.", code, AppInfo.VersionLabel));
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        { _logger.LogWarning("Update result could not be read: {ErrorType}", error.GetType().Name); }
    }

    private bool CanCheckNow() => !IsCheckingForUpdates && !IsUpdateBusy;
    [RelayCommand(CanExecute = nameof(CanCheckNow))]
    private Task CheckForUpdatesNowAsync() => RunUpdateCheckAsync(manual: true);

    /// <summary>
    /// Asks the release page whether a newer version exists. The automatic check respects the toggle and runs at most every twelve hours; the manual
    /// check always runs. Failures are quiet unless the user asked.
    /// </summary>
    public async Task RunUpdateCheckAsync(bool manual)
    {
        if (IsCheckingForUpdates || IsUpdateBusy) return;
        if (!manual && (!AutoCheckUpdates || (_lastUpdateCheck is { } last && DateTimeOffset.UtcNow - last < UpdateCheckInterval))) return;
        IsCheckingForUpdates = true;
        if (manual) SetUpdateStatus(() => Loc.T("Checking for updates…"));
        try
        {
            var offer = await _updates.CheckAsync(AppInfo.Version);
            _lastUpdateCheck = DateTimeOffset.UtcNow;
            if (offer is null)
            {
                _updateOffer = null; UpdateBannerVisible = false;
                var checkedAt = _lastUpdateCheck.Value;
                SetUpdateStatus(() => Loc.T("You are running the latest version ({0}). Checked {1:g}.", AppInfo.VersionLabel, checkedAt.ToLocalTime()));
            }
            else
            {
                _updateOffer = offer;
                if (manual) _skippedUpdateVersion = null;
                var hidden = !manual && offer.VersionText == _skippedUpdateVersion;
                SetUpdateStatus(() => hidden ? Loc.T("Version {0} is available. You chose to skip it; check manually to see it again.", offer.VersionText) : Loc.T("Version {0} is available.", offer.VersionText));
                if (!hidden) ShowUpdateOffer(offer);
            }
            InstallUpdateCommand.NotifyCanExecuteChanged();
            Persist();
        }
        catch (Exception error)
        {
            _logger.LogInformation("Update check failed: {ErrorType}", error.GetType().Name);
            if (manual) SetUpdateStatus(() => Loc.T("Could not check for updates: {0}", FriendlyUpdateError(error)));
        }
        finally { IsCheckingForUpdates = false; }
    }

    private void ShowUpdateOffer(UpdateOffer offer)
    {
        UpdateTitle = Loc.T("Version {0} is available", offer.VersionText);
        var notes = string.IsNullOrWhiteSpace(offer.Notes) ? "" : offer.Notes + "\n";
        UpdateDetail = notes + Loc.T("You are running {0}. Download size {1:0} MiB, checked against its SHA256 before it runs.", AppInfo.VersionLabel, offer.Bytes / 1048576d)
            + (CanSelfUpdate ? "" : " " + Loc.T("This copy is not the installed app, so it cannot update itself; use the installer from the release page."));
        UpdatePercent = 0;
        UpdateBannerVisible = true;
    }

    private bool CanInstallUpdate() => _updateOffer is not null && !IsUpdateBusy && CanSelfUpdate;
    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task InstallUpdateAsync()
    {
        if (_updateOffer is not { } offer) return;
        IsUpdateBusy = true; UpdatePercent = 0;
        _updateCancellation = new();
        var token = _updateCancellation.Token;
        try
        {
            SetUpdateDetail(() => Loc.T("Downloading the update…"));
            var progress = new Progress<DownloadProgress>(value =>
            {
                UpdatePercent = value.Total == 0 ? 0 : value.Received * 100d / value.Total;
                SetUpdateDetail(() => Loc.T("Downloading {0:0} / {1:0} MiB · checked against SHA256 before it runs", value.Received / 1048576d, value.Total / 1048576d));
            });
            var setup = await _updates.DownloadAsync(offer, Edition.UpdateDirectory, progress, token);
            token.ThrowIfCancellationRequested();
            UpdatePercent = 100;
            SetUpdateDetail(() => Loc.T("Verified. Mockingbird Studio will now close, install the update and reopen."));
            UpdateLauncher.Start(setup, Environment.ProcessPath ?? throw new InvalidOperationException(Loc.T("The app location is unknown.")),
                Environment.ProcessId, UpdateResultPath, offer.VersionText, UpdateRelaunchArguments);
            System.Windows.Application.Current.Shutdown();
        }
        catch (OperationCanceledException) { SetUpdateDetail(() => Loc.T("Update cancelled. Nothing was installed.")); }
        catch (Exception error)
        {
            _logger.LogWarning("Update install failed: {ErrorType}", error.GetType().Name);
            ReportError(Loc.T("The update could not be installed"), FriendlyUpdateError(error));
            SetUpdateDetail(() => Loc.T("The update did not finish. You are still on {0}.", AppInfo.VersionLabel));
        }
        finally { _updateCancellation?.Dispose(); _updateCancellation = null; IsUpdateBusy = false; }
    }

    /// <summary>Arguments for the app the helper starts after the installer finishes. Normally none.</summary>
    public string? UpdateRelaunchArguments { get; set; }

    [RelayCommand] private void CancelUpdate() => _updateCancellation?.Cancel();
    private bool CanDismissUpdate() => !IsUpdateBusy;
    [RelayCommand(CanExecute = nameof(CanDismissUpdate))] private void DismissUpdate() => UpdateBannerVisible = false;
    [RelayCommand(CanExecute = nameof(CanDismissUpdate))]
    private void SkipUpdate()
    {
        if (_updateOffer is { } offer) _skippedUpdateVersion = offer.VersionText;
        UpdateBannerVisible = false;
        Persist();
    }

    private static string FriendlyUpdateError(Exception error) => error switch
    {
        HttpRequestException => Loc.T("GitHub could not be reached. Check your internet connection and try again."),
        OperationCanceledException => Loc.T("The update server did not respond in time."),
        _ => Loc.Describe(error.Message)
    };
}
