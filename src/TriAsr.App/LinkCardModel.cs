using System.IO;
using System.Net.Http;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TriAsr.Application;
using TriAsr.Infrastructure;

namespace TriAsr.App;

/// <summary>
/// What the link card shows: a box for the address of a video or audio on the web, one button, how far it has come, and a note when something
/// stands in the way. Studio's New page fetches the sound on this computer (<see cref="LinkViewModel"/>); the Client's New tab and Studio's Remote page ask the
/// server to fetch it (<see cref="RemoteLinkViewModel"/>). Both cards look the same.
/// </summary>
public abstract partial class LinkCardModel : ObservableObject
{
    private CancellationTokenSource? _cancellation;
    private Func<string>? _statusMake;

    [ObservableProperty] private string _linkText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _status = "";

    protected LinkCardModel()
    {
        RunCommand = new AsyncRelayCommand(RunCoreAsync, CanRun);
        CancelCommand = new RelayCommand(() => _cancellation?.Cancel(), () => IsBusy);
    }

    public IAsyncRelayCommand RunCommand { get; }
    public IRelayCommand CancelCommand { get; }

    /// <summary>The sentence under the title.</summary>
    public abstract string Description { get; }

    /// <summary>The label of the one button.</summary>
    public abstract string ActionLabel { get; }

    /// <summary>Something that stands in the way, or something the person should know first; empty when there is nothing.</summary>
    public virtual string Note => "";
    public bool HasNote => Note.Length > 0;
    public virtual string NoteActionLabel => "";
    public virtual ICommand? NoteCommand => null;
    public bool HasNoteAction => NoteCommand is not null && NoteActionLabel.Length > 0;

    protected virtual bool CanRunNow() => true;

    private bool CanRun() => !IsBusy && LinkText.Trim().Length > 0 && CanRunNow();

    partial void OnLinkTextChanged(string value) => RunCommand.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value) { RunCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); }

    /// <summary>Tells the card that the situation changed (a server was connected, the helper was installed) so that the button and the note are looked at again.</summary>
    protected void SituationChanged()
    {
        RunCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(Note)); OnPropertyChanged(nameof(HasNote)); OnPropertyChanged(nameof(NoteActionLabel)); OnPropertyChanged(nameof(HasNoteAction)); OnPropertyChanged(nameof(NoteCommand));
    }

    protected void SetStatus(Func<string>? make) { _statusMake = make; Status = make?.Invoke() ?? ""; }

    private async Task RunCoreAsync()
    {
        if (IsBusy) return;
        _cancellation = new CancellationTokenSource();
        IsBusy = true; Percent = 0;
        try { await ExecuteAsync(LinkText.Trim(), _cancellation.Token); }
        catch (OperationCanceledException) { SetStatus(() => Loc.T("Cancelled")); }
        finally { IsBusy = false; _cancellation.Dispose(); _cancellation = null; }
    }

    /// <summary>Does what the button does. Reports failures through <see cref="SetStatus"/> and lets only cancellation escape.</summary>
    protected abstract Task ExecuteAsync(string text, CancellationToken token);

    /// <summary>Builds the texts again after the interface language changed.</summary>
    public virtual void RefreshTexts()
    {
        OnPropertyChanged(nameof(Description)); OnPropertyChanged(nameof(ActionLabel));
        SituationChanged();
        if (_statusMake is not null) Status = _statusMake();
    }

    /// <summary>The failures that are reported to the person (a message from a layer below goes through <see cref="Loc.Describe"/>); anything else is a bug and is not hidden.</summary>
    protected static bool IsExpected(Exception error) => error is IOException or UnauthorizedAccessException or HttpRequestException;
}

/// <summary>Studio's link card: the sound is fetched on this computer and chosen as the recording to transcribe.</summary>
public sealed class LinkViewModel : LinkCardModel
{
    private readonly ILinkFetcher _fetcher;
    private readonly string _folder;
    private readonly Action<Action> _onUi;

    public LinkViewModel(ILinkFetcher fetcher, LinkHelperViewModel helper, string folder, Action<Action> onUi)
    {
        _fetcher = fetcher; Helper = helper; _folder = folder; _onUi = onUi;
        helper.PropertyChanged += (_, _) => _onUi(SituationChanged);
    }

    public LinkHelperViewModel Helper { get; }

    /// <summary>Raised on the window's thread when the sound has been fetched. The file is complete.</summary>
    public event Action<FetchedLink>? Fetched;

    public override string Description => Loc.T("Paste the address of a video or audio on the web: a video site, a podcast episode or a link to an audio file. Its sound is downloaded to this computer and chosen as the recording above.");
    public override string ActionLabel => Loc.T("Fetch the sound");

    // The helper is offered while it is missing, and while it works.
    public override string Note => Helper.Installed && !Helper.IsBusy ? "" : Helper.Summary;
    public override string NoteActionLabel => Helper.Installed ? "" : Helper.ActionLabel;
    public override System.Windows.Input.ICommand? NoteCommand => Helper.Installed ? null : Helper.RunCommand;

    protected override async Task ExecuteAsync(string text, CancellationToken token)
    {
        Uri link;
        try { link = LinkPolicy.Parse(text); }
        catch (LinkException error) { var reason = error.Message; SetStatus(() => Loc.Describe(reason)); return; }
        SetStatus(() => Loc.T("Fetching the sound…"));
        var folder = Path.Combine(_folder, Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var file = await _fetcher.FetchAsync(link, folder, new LinkFetchOptions(AllowPrivateNetwork: true), new Progress<double>(value => _onUi(() => Percent = value)), token);
            var name = Path.GetFileName(file.Path);
            SetStatus(() => Loc.T("Fetched: {0}", name));
            LinkText = "";
            Fetched?.Invoke(file);
        }
        catch (OperationCanceledException) { DeleteQuietly(folder); throw; }
        catch (LinkException error) { DeleteQuietly(folder); var reason = error.Message; SetStatus(() => Loc.Describe(reason)); }
        catch (Exception error) when (IsExpected(error)) { DeleteQuietly(folder); var reason = error.Message; SetStatus(() => Loc.T("The link could not be downloaded: {0}", Loc.Describe(reason))); }
    }

    private static void DeleteQuietly(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public override void RefreshTexts() { Helper.RefreshTexts(); base.RefreshTexts(); }
}

/// <summary>The link card of a connected server: the server fetches the sound and transcribes it, so nothing is downloaded here.</summary>
public sealed class RemoteLinkViewModel : LinkCardModel
{
    private readonly RemoteWorkspaceViewModel _workspace;

    public RemoteLinkViewModel(RemoteWorkspaceViewModel workspace) => _workspace = workspace;

    public override string Description => Loc.T("Paste the address of a video or audio on the web: a video site, a podcast episode or a link to an audio file. The server downloads its sound and transcribes it.");
    public override string ActionLabel => Loc.T("Send link to server");

    private RemoteServerInfo? Info => _workspace.ServerInfo;

    public override string Note => Info switch
    {
        null => "",
        { LinksEnabled: true, LinkPages: true } => "",
        { LinksEnabled: true } => Loc.T("This server fetches links to audio and video files. To fetch the sound of web pages as well, install the link helper in Mockingbird on the server."),
        { PasswordRequired: false } => Loc.T("This server has no password, so it does not fetch links for other computers. Set a password on the server to allow it."),
        _ => Loc.T("This server does not fetch links.")
    };

    protected override bool CanRunNow() => _workspace.IsConnected && Info is { LinksEnabled: true, ModelsReady: true };

    /// <summary>The connection or the server's answer changed.</summary>
    public void ServerChanged() => SituationChanged();

    protected override async Task ExecuteAsync(string text, CancellationToken token)
    {
        try { LinkPolicy.Parse(text); }
        catch (LinkException error) { var reason = error.Message; SetStatus(() => Loc.Describe(reason)); return; }
        if (_workspace.Client is not { } client) return;
        var server = _workspace.ServerName;
        SetStatus(() => Loc.T("Sending to {0}…", server));
        try
        {
            var job = await client.SendLinkAsync(text, _workspace.LanguageChoice, token, _workspace.SpeakersChoice);
            _workspace.ShowSent(job);
            LinkText = "";
            SetStatus(() => Loc.T("Sent. The server is working on it."));
        }
        catch (RemoteException error) when (error.Code == "models_missing")
        {
            SetStatus(() => Loc.T("The server cannot transcribe this language yet: its speech models are not downloaded."));
        }
        catch (RemoteException error)
        {
            var reason = error.Message;
            SetStatus(() => Loc.T("The link could not be sent: {0}", Loc.Describe(reason)));
            if (error.IsUnreachable) _workspace.LoseConnection(error.Message);
        }
    }
}
