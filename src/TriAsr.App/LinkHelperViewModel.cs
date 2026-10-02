using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TriAsr.Application;

namespace TriAsr.App;

/// <summary>
/// The helper program that fetches the sound of web pages (yt-dlp), as the person sees it: installed or not, and the one button that installs it or looks for
/// a newer one. Pressing the button is the consent to download it; the text next to it says from where and how large.
/// Studio's link card, Studio's Servers page and the Server window show it.
/// </summary>
public sealed partial class LinkHelperViewModel(ILinkTool tool, Action<Action> onUi) : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Func<string>? _statusMake;
    private bool _installed = tool.Installed;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _percent;

    public bool Installed => _installed;
    public bool NotInstalled => !Installed;

    /// <summary>What the helper is doing or has just done; when there is nothing to report, what it is and whether it is installed.</summary>
    public string Summary => _statusMake?.Invoke() ?? (Installed
        ? Loc.T("The link helper is installed. It fetches the sound of web pages.")
        : Loc.T("The link helper is not installed. Links straight to audio and video files work without it; links to web pages need it. Installing downloads it once from github.com/yt-dlp/yt-dlp (about 20 MB) and checks it against the checksum published there."));

    public string ActionLabel => Installed ? Loc.T("Check for a newer link helper") : Loc.T("Install the link helper");

    private void SetStatus(Func<string>? make) { _statusMake = make; OnPropertyChanged(nameof(Summary)); }

    /// <summary>Installs the helper, or replaces it when its project has published a newer one.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        var wasInstalled = Installed;
        IsBusy = true; Percent = 0;
        SetStatus(() => Loc.T("Downloading the link helper… {0}%", (int)Percent));
        try
        {
            var changed = await tool.InstallAsync(new Progress<double>(value => onUi(() => { Percent = value; OnPropertyChanged(nameof(Summary)); })), _stop.Token);
            SetStatus(changed ? (wasInstalled ? () => Loc.T("The link helper was updated.") : () => Loc.T("The link helper was installed.")) : () => Loc.T("The link helper is up to date."));
        }
        catch (OperationCanceledException) { SetStatus(null); }
        catch (Exception error) when (error is LinkException or IOException or UnauthorizedAccessException or HttpRequestException)
        {
            var reason = error.Message;
            SetStatus(() => Loc.T("The link helper could not be installed: {0}", Loc.Describe(reason)));
        }
        finally
        {
            IsBusy = false;
            _installed = tool.Installed;
            OnPropertyChanged(nameof(Installed)); OnPropertyChanged(nameof(NotInstalled)); OnPropertyChanged(nameof(ActionLabel)); OnPropertyChanged(nameof(Summary));
        }
    }

    private bool CanRun() => !IsBusy;

    partial void OnIsBusyChanged(bool value) => RunCommand.NotifyCanExecuteChanged();

    /// <summary>Builds the texts again after the interface language changed.</summary>
    public void RefreshTexts() { OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(ActionLabel)); }

    public void Dispose() { _stop.Cancel(); _stop.Dispose(); }
}
