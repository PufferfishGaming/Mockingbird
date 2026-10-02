using System.IO;
using TriAsr.Infrastructure;

namespace TriAsr.App;

/// <summary>
/// The servers on the network and the connection to one of them, wired together: a connection that works is handed to the pages of the server, a server
/// that stops answering is reported back to the list. Studio and the Client edition each own one.
/// </summary>
public sealed class RemoteSession
{
    /// <param name="dataRoot">Where the remembered servers and the temporary audio of the review are kept.</param>
    /// <param name="reportError">Shows a problem (title, message) in the window.</param>
    /// <param name="reviewSaved">Called when edits were saved on the server.</param>
    public RemoteSession(string dataRoot, Action<string, string> reportError, Action reviewSaved)
    {
        Servers = new ServerBrowserViewModel(new SavedServerStore(Path.Combine(dataRoot, "Config", "servers.json")), new ForwardedDialogs(() => Dialogs), OnUi);
        Remote = new RemoteWorkspaceViewModel(OnUi, (title, message) => OnUi(() => reportError(title, message)), Path.Combine(dataRoot, "Temp", "Remote"), Path.Combine(dataRoot, "Recordings"), dataRoot);
        Remote.ConnectionLost += reason => OnUi(() => Servers.Lost(reason));
        Remote.ReviewSaved += () => OnUi(reviewSaved);
        Servers.ConnectionChanged += connection => OnUi(() => { Remote.Attach(connection); ConnectionChanged?.Invoke(connection); });
    }

    public ServerBrowserViewModel Servers { get; }

    /// <summary>The pages of the connected server (send, projects, review).</summary>
    public RemoteWorkspaceViewModel Remote { get; }

    /// <summary>The questions a connection asks (trust this fingerprint, enter the password). The window sets the real dialogs; until then nothing is trusted.</summary>
    public IServerDialogs? Dialogs { get; set; }

    /// <summary>Raised on the window's thread after the pages have been attached to the new connection, or detached (null) when it ended.</summary>
    public event Action<RemoteConnection?>? ConnectionChanged;

    public static void OnUi(Action action)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess()) dispatcher.Invoke(action);
        else action();
    }

    /// <summary>Stops listening for servers and drops the connection.</summary>
    public void Close()
    {
        Remote.Dispose();
        _ = Servers.DisposeAsync().AsTask();
    }

    /// <summary>Passes the connection's questions on to whatever dialogs the window has set; with none, the answer is no.</summary>
    private sealed class ForwardedDialogs(Func<IServerDialogs?> current) : IServerDialogs
    {
        public Task<bool> ConfirmTrustAsync(TrustRequest request) => current() is { } dialogs ? dialogs.ConfirmTrustAsync(request) : Task.FromResult(false);
        public Task<PasswordAnswer?> AskPasswordAsync(string serverName, bool wrongBefore) => current() is { } dialogs ? dialogs.AskPasswordAsync(serverName, wrongBefore) : Task.FromResult<PasswordAnswer?>(null);
    }
}
