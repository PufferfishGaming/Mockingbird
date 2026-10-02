using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TriAsr.Infrastructure;

namespace TriAsr.App;

/// <summary>What the user is asked when a server shows a certificate nobody has confirmed (or one that is not the one that was).</summary>
/// <param name="Changed">The server was trusted before and now shows another certificate.</param>
public sealed record TrustRequest(string Name, Uri Address, string? Fingerprint, bool Changed, bool Encrypted);

public sealed record PasswordAnswer(string Password, bool Remember);

/// <summary>The questions a connection can ask. The window shows them as dialogs; tests answer them.</summary>
public interface IServerDialogs
{
    /// <summary>Whether to trust the server, shown with its fingerprint (or a warning that the connection is not encrypted).</summary>
    Task<bool> ConfirmTrustAsync(TrustRequest request);

    /// <summary>The password for a server, or null to give up. <paramref name="wrongBefore"/> is true when the last one was refused.</summary>
    Task<PasswordAnswer?> AskPasswordAsync(string serverName, bool wrongBefore);
}

/// <summary>A connection that works: the client, what the server said about itself, and the entry it belongs to.</summary>
public sealed record RemoteConnection(RemoteServerClient Client, RemoteServerInfo Info, ServerEntry Entry);

/// <summary>One server in the list: heard on the network, saved from an earlier connection, or typed in.</summary>
public sealed partial class ServerEntry : ObservableObject
{
    public ServerEntry(string id, string name, Uri address) { Id = id; _name = name; _address = address; }

    public string Id { get; }
    [ObservableProperty] private string _name;
    [ObservableProperty] private Uri _address;
    [ObservableProperty] private string _edition = "";
    [ObservableProperty] private string _version = "";
    [ObservableProperty] private bool _passwordRequired;
    [ObservableProperty] private bool _isThisComputer;
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private bool _isSaved;
    [ObservableProperty] private bool _isConnected;
    /// <summary>The certificate that was trusted for this server, or empty.</summary>
    public string Pinned { get; set; } = "";
    /// <summary>The password the user asked to remember, in the clear in memory only.</summary>
    public string? Password { get; set; }

    public string Detail => string.Join(" · ", new[]
    {
        $"{Address.Host}:{Address.Port}",
        Edition.Length > 0 ? Edition : null,
        Address.Scheme == "https" ? Loc.T("encrypted") : Loc.T("not encrypted"),
        PasswordRequired ? Loc.T("password") : null,
        IsThisComputer ? Loc.T("this computer") : null,
        IsOnline ? null : Loc.T("not seen lately")
    }.Where(part => part is not null));

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Detail));
    partial void OnAddressChanged(Uri value) => OnPropertyChanged(nameof(Detail));
    partial void OnEditionChanged(string value) => OnPropertyChanged(nameof(Detail));
    partial void OnPasswordRequiredChanged(bool value) => OnPropertyChanged(nameof(Detail));
    partial void OnIsThisComputerChanged(bool value) => OnPropertyChanged(nameof(Detail));
    partial void OnIsOnlineChanged(bool value) => OnPropertyChanged(nameof(Detail));

    public void RefreshTexts() => OnPropertyChanged(nameof(Detail));
}

/// <summary>
/// The servers on the network and the one this window is connected to: servers are heard from their announcements, remembered after a
/// connection, or typed in by address. A connection first looks at the server without sending anything secret, asks the user to confirm its
/// fingerprint the first time (and again, with a warning, if it changes), and only then sends the password.
/// </summary>
public sealed partial class ServerBrowserViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SavedServerStore _store;
    private readonly IServerDialogs _dialogs;
    private readonly Action<Action> _onUi;
    private readonly int _beaconPort;
    private readonly TimeSpan? _beaconLifetime;
    private BeaconListener? _listener;

    public ServerBrowserViewModel(SavedServerStore store, IServerDialogs dialogs, Action<Action> onUi, int beaconPort = ServerBeacon.DefaultPort, TimeSpan? beaconLifetime = null)
    {
        _store = store; _dialogs = dialogs; _onUi = onUi; _beaconPort = beaconPort; _beaconLifetime = beaconLifetime;
        Loc.Instance.PropertyChanged += (_, change) => { if (change.PropertyName == nameof(Loc.Version)) _onUi(RefreshTexts); };
    }

    public ObservableCollection<ServerEntry> Servers { get; } = [];
    [ObservableProperty] private ServerEntry? _selected;
    [ObservableProperty] private string _addressText = "";
    [ObservableProperty] private string _status = Loc.Key("Not connected.");
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private ServerEntry? _connected;

    public RemoteConnection? Connection { get; private set; }
    public bool IsListening { get; private set; }

    /// <summary>Raised on the window's thread when a connection is made, or ended (null).</summary>
    public event Action<RemoteConnection?>? ConnectionChanged;

    public bool IsEmpty => Servers.Count == 0;
    public bool HasConnection => Connected is not null;

    /// <summary>Loads the servers remembered from earlier and starts listening for announcements.</summary>
    public void Start()
    {
        foreach (var saved in _store.Load())
        {
            var entry = new ServerEntry(saved.Id, saved.Name, new Uri(saved.Address)) { Pinned = saved.Fingerprint, IsSaved = true };
            if (saved.ProtectedPassword is { } protectedText) entry.Password = Dpapi.Unprotect(protectedText);
            Servers.Add(entry);
        }
        _listener = new BeaconListener(_beaconPort, _beaconLifetime);
        IsListening = _listener.Start();
        _listener.Changed += () => _onUi(ApplyHeard);
        ApplyHeard();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void ApplyHeard()
    {
        if (_listener is null) return;
        var heard = _listener.Servers;
        foreach (var entry in Servers) entry.IsOnline = false;
        foreach (var server in heard)
        {
            var entry = Servers.FirstOrDefault(item => item.Id == server.Id) ?? Servers.FirstOrDefault(item => item.Address.Host == server.Address.ToString() && item.Address.Port == server.Announcement.Port);
            if (entry is null) { entry = new ServerEntry(server.Id, server.Announcement.Name, server.Uri); Servers.Add(entry); }
            entry.Name = server.Announcement.Name; entry.Address = server.Uri; entry.Edition = server.Announcement.Edition; entry.Version = server.Announcement.Version;
            entry.PasswordRequired = server.Announcement.PasswordRequired; entry.IsThisComputer = server.ThisComputer; entry.IsOnline = true;
        }
        // A server that was never saved and has gone quiet leaves the list; a saved one stays, marked as not seen.
        foreach (var gone in Servers.Where(item => !item.IsOnline && !item.IsSaved && !item.IsConnected && !item.Id.StartsWith("manual:")).ToArray()) Servers.Remove(gone);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void RefreshTexts()
    {
        foreach (var entry in Servers) entry.RefreshTexts();
        RefreshStatus();
    }

    // ---- adding by address ---------------------------------------------------------------------------------------------------------

    /// <summary>The addresses to try for what was typed: <c>host</c>, <c>host:port</c> or a full address. Encrypted first.</summary>
    public static IReadOnlyList<Uri> Candidates(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return [];
        if (text.Contains("://"))
            return Uri.TryCreate(text, UriKind.Absolute, out var full) && full.Scheme is "http" or "https" && full.Host.Length > 0 ? [new Uri(full.GetLeftPart(UriPartial.Authority))] : [];
        if (!Uri.TryCreate("https://" + text, UriKind.Absolute, out var parsed) || parsed.Host.Length == 0) return [];
        var port = parsed.IsDefaultPort ? HostViewModel.DefaultPort : parsed.Port;   // no port typed: the one servers use unless told otherwise
        return [new Uri($"https://{parsed.Host}:{port}"), new Uri($"http://{parsed.Host}:{port}")];
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var candidates = Candidates(AddressText);
        if (candidates.Count == 0) { Status = Loc.T("That address is not valid."); return; }
        IsConnecting = true; Status = Loc.T("Looking for a server at {0}…", AddressText.Trim());
        try
        {
            RemoteProbe? found = null; RemoteException? last = null;
            foreach (var candidate in candidates)
            {
                try { found = await RemoteServerClient.ProbeAsync(candidate, CancellationToken.None); break; }
                catch (RemoteException error) { last = error; }
            }
            if (found is null) { Status = Loc.T("No server answered at {0}: {1}", AddressText.Trim(), last is null ? "" : Loc.Describe(last.Message)); return; }
            var id = "manual:" + found.Address.Authority;
            var entry = Servers.FirstOrDefault(item => item.Address.Authority == found.Address.Authority) ?? new ServerEntry(id, found.Health.Name, found.Address);
            entry.Name = found.Health.Name; entry.Address = found.Address; entry.Edition = found.Health.Edition; entry.PasswordRequired = found.Health.PasswordRequired; entry.IsOnline = true;
            if (!Servers.Contains(entry)) Servers.Add(entry);
            Selected = entry; AddressText = ""; OnPropertyChanged(nameof(IsEmpty));
            Status = Loc.T("Found {0}. Choose Connect.", entry.Name);
        }
        finally { IsConnecting = false; }
    }

    // ---- connecting ----------------------------------------------------------------------------------------------------------------

    /// <summary>Looks at the server, asks what must be asked, and connects. Returns the connection, or null when the user said no or it did not work.</summary>
    public async Task<RemoteConnection?> ConnectAsync(ServerEntry entry)
    {
        if (IsConnecting) return null;
        IsConnecting = true;
        Status = Loc.T("Connecting to {0}…", entry.Name);
        try
        {
            if (Connection is not null) Disconnect();
            RemoteProbe probe;
            try { probe = await RemoteServerClient.ProbeAsync(entry.Address, CancellationToken.None); }
            catch (RemoteException error) { Status = Loc.T("{0} could not be reached: {1}", entry.Name, Loc.Describe(error.Message)); return null; }

            // 1. Who is it? An encrypted server is recognised by its certificate; an unencrypted one only gets a warning.
            var address = probe.Address;
            var fingerprint = probe.Fingerprint;
            var encrypted = fingerprint is not null;
            var known = encrypted && entry.Pinned.Length > 0 && ServerIdentity.Same(entry.Pinned, fingerprint);
            if (!known && (encrypted || !IPAddress.TryParse(address.Host, out var ip) || !IPAddress.IsLoopback(ip)))
            {
                var trusted = await _dialogs.ConfirmTrustAsync(new TrustRequest(probe.Health.Name, address, fingerprint, encrypted && entry.Pinned.Length > 0, encrypted));
                if (!trusted) { Status = Loc.T("Not connected: the server was not trusted."); return null; }
            }
            if (encrypted) entry.Pinned = fingerprint!;

            // 2. The password, if the server has one: the remembered one first, then asking, a few times.
            string? password = null;
            if (probe.Health.PasswordRequired) password = entry.Password;
            for (var attempt = 0; ; attempt++)
            {
                if (probe.Health.PasswordRequired && string.IsNullOrEmpty(password))
                {
                    var answer = await _dialogs.AskPasswordAsync(probe.Health.Name, attempt > 0);
                    if (answer is null) { Status = Loc.T("Not connected: no password was given."); return null; }
                    password = answer.Password;
                    entry.Password = answer.Remember ? password : null;
                }
                var client = new RemoteServerClient(address, password, encrypted ? fingerprint : null);
                try
                {
                    var info = await client.InfoAsync(CancellationToken.None);
                    entry.Name = info.Name; entry.Address = address; entry.Edition = info.Edition; entry.Version = info.Version; entry.PasswordRequired = info.PasswordRequired;
                    entry.IsSaved = true; entry.IsOnline = true; entry.IsConnected = true;
                    Save();
                    Connected = entry; Connection = new RemoteConnection(client, info, entry);
                    Status = Loc.T("Connected to {0}", info.Name);
                    OnPropertyChanged(nameof(HasConnection));
                    ConnectionChanged?.Invoke(Connection);
                    return Connection;
                }
                catch (RemoteException error) when (error.IsAuthentication && attempt < 3)
                {
                    client.Dispose(); password = null; entry.Password = null;
                    if (error.Code == "too_many_attempts") { Status = Loc.T("Too many wrong passwords. Try again in a minute."); return null; }
                }
                catch (RemoteException error)
                {
                    client.Dispose();
                    Status = error.IsIdentityChanged ? Loc.T("Not connected: the server showed a different identity.") : Loc.T("{0} could not be reached: {1}", entry.Name, Loc.Describe(error.Message));
                    return null;
                }
            }
        }
        finally { IsConnecting = false; }
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private Task ConnectSelectedAsync() => Selected is { } entry ? ConnectAsync(entry) : Task.CompletedTask;

    private bool CanConnect() => Selected is not null && !IsConnecting;

    partial void OnSelectedChanged(ServerEntry? value) => ConnectSelectedCommand.NotifyCanExecuteChanged();
    partial void OnIsConnectingChanged(bool value) => ConnectSelectedCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    public void Disconnect()
    {
        var connection = Connection;
        Connection = null;
        if (Connected is { } entry) entry.IsConnected = false;
        Connected = null;
        connection?.Client.Dispose();
        Status = Loc.T("Not connected.");
        OnPropertyChanged(nameof(HasConnection));
        ConnectionChanged?.Invoke(null);
    }

    /// <summary>The connection broke (the server went away); the list says so instead of pretending.</summary>
    public void Lost(string reason)
    {
        Disconnect();
        Status = Loc.T("The connection was lost: {0}", reason);
    }

    /// <summary>Forgets the selected server: what was trusted and the password. A server still on the network reappears, to be trusted again.</summary>
    [RelayCommand]
    private void Forget()
    {
        if (Selected is not { } entry) return;
        if (entry.IsConnected) Disconnect();
        entry.IsSaved = false; entry.Pinned = ""; entry.Password = null;
        Save();
        if (!entry.IsOnline) Servers.Remove(entry);
        Selected = null;
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void Save() => _store.Save(Servers.Where(item => item.IsSaved).Select(item =>
        new SavedServer(item.Id, item.Name, item.Address.ToString().TrimEnd('/'), item.Pinned, item.Password is { Length: > 0 } password ? Dpapi.Protect(password) : null)));

    private void RefreshStatus()
    {
        if (Connected is { } entry) Status = Loc.T("Connected to {0}", entry.Name);
        else if (Status == Loc.TextIn(Loc.Instance.Previous, "Not connected.")) Status = Loc.T("Not connected.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Connection is not null) { Connection.Client.Dispose(); Connection = null; }
        if (_listener is not null) await _listener.DisposeAsync();
    }
}
