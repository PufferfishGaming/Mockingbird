using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TriAsr.Domain;
using TriAsr.Infrastructure;

namespace TriAsr.App;

/// <summary>
/// Hosting this computer's transcription for others: a name, an optional password, whether the network may reach it, and the
/// port. The server speaks TLS with a certificate of its own whose fingerprint clients are shown; it announces itself on the local network only
/// when the network is allowed. Used by Studio (in its server panel) and by the Server edition (its whole window).
/// </summary>
public sealed partial class HostViewModel : ObservableObject, IAsyncDisposable
{
    public const int DefaultPort = 8642;

    /// <param name="createService">Builds the API behind the server; the host passes itself so the service can read the password and name as they are now.</param>
    public HostViewModel(string dataRoot, string edition, string version, Func<HostViewModel, ApiService> createService, Action<string, string> reportError, Action<Action> onUi, ILogger logger)
    {
        _identityFolder = Path.Combine(dataRoot, "Config", "Identity");
        _edition = edition; _version = version; _createService = createService; _reportError = reportError; _onUi = onUi; _logger = logger;
    }

    private readonly string _identityFolder, _edition, _version;
    private readonly Func<HostViewModel, ApiService> _createService;
    private readonly Action<string, string> _reportError;
    private readonly Action<Action> _onUi;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _restoring, _started;
    private ApiService? _service;
    private LocalHttpServer? _server;
    private BeaconSender? _beacon;
    private ServerIdentity? _identity;

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _allowNetwork;
    [ObservableProperty] private string _portText = DefaultPort.ToString();
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _status = Loc.Key("The server is off.");
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _fingerprint = "";
    /// <summary>The network address the QR code shows, when this server has more than one (a cable and Wi-Fi, say).</summary>
    [ObservableProperty] private string? _selectedPhoneAddress;
    /// <summary>The QR code of <see cref="SelectedPhoneAddress"/>: a phone on the same network opens the web page by pointing its camera at it.</summary>
    [ObservableProperty] private ImageSource? _phoneCode;

    private enum State { Off, Listening, BadPort, Failed }
    private State _state;
    private string _failure = "";

    /// <summary>The UDP port announcements go to. Only a test changes it.</summary>
    public int BeaconPort { get; set; } = ServerBeacon.DefaultPort;

    /// <summary>Stays the same for this installation, so that a renamed server is still the same server on the network.</summary>
    public string ServerId { get; private set; } = Guid.NewGuid().ToString("N");

    /// <summary>Raised when a setting was changed by the user and should be saved.</summary>
    public event Action? SettingsChanged;

    /// <summary>The helper that fetches the sound of web pages for the links other computers send. Set by the window that owns the services; null where there is none.</summary>
    public LinkHelperViewModel? LinkHelper { get; set; }

    /// <summary>Raised when the API itself changes a job (cancelling one that never started), so that lists in the window can follow.</summary>
    public event EventHandler<TranscriptionJob>? JobChangedByApi;

    /// <summary>A client deleted a recording through the API; the window drops it from its list.</summary>
    public event EventHandler<Guid>? JobDeletedByApi;

    /// <summary>The window deleted a project: the API stops listing it.</summary>
    public void Forget(Guid id) => _service?.Forget(id);

    public int Port => int.TryParse(PortText, out var port) && port is >= 1024 and <= 65535 ? port : 0;
    public bool HasPassword => Password.Length > 0;
    public string DisplayName => Name.Trim().Length > 0 ? Name.Trim() : Environment.MachineName;
    public bool HasFingerprint => Fingerprint.Length > 0;
    public bool IsListening => _state == State.Listening;
    public ApiService? Service => _service;

    public void Restore(AppSettings settings)
    {
        _restoring = true;
        ServerId = string.IsNullOrWhiteSpace(settings.HostId) ? ServerId : settings.HostId;
        Name = settings.HostName; PortText = settings.HostPort.ToString(); Password = settings.HostPassword; AllowNetwork = settings.HostAllowNetwork; Enabled = settings.HostEnabled;
        _restoring = false;
    }

    public AppSettings Write(AppSettings settings) => settings with
    {
        HostEnabled = Enabled, HostName = Name.Trim(), HostPort = Port == 0 ? DefaultPort : Port, HostAllowNetwork = AllowNetwork, HostPassword = Password, HostId = ServerId
    };

    partial void OnEnabledChanged(bool value) => Changed();
    partial void OnAllowNetworkChanged(bool value) { OnPropertyChanged(nameof(LocalOnly)); Changed(); }
    partial void OnPortTextChanged(string value) { OnPropertyChanged(nameof(Example)); Changed(); }
    // The name and the password are read afresh by the beacon and the API each time, so changing them needs no restart.
    partial void OnNameChanged(string value) { OnPropertyChanged(nameof(DisplayName)); SettingsChangedOnly(); }
    partial void OnPasswordChanged(string value) { OnPropertyChanged(nameof(HasPassword)); OnPropertyChanged(nameof(Example)); SettingsChangedOnly(); }
    partial void OnFingerprintChanged(string value) => OnPropertyChanged(nameof(HasFingerprint));
    partial void OnSelectedPhoneAddressChanged(string? value) => PhoneCode = value is null ? null : QrCodes.Image(value);
    partial void OnPhoneCodeChanged(ImageSource? value) => OnPropertyChanged(nameof(HasPhoneCode));
    partial void OnIsBusyChanged(bool value) => RefreshStatus();

    private void SettingsChangedOnly() { if (!_restoring) SettingsChanged?.Invoke(); }

    private void Changed()
    {
        if (_restoring) return;
        SettingsChanged?.Invoke();
        if (_started) _ = RestartAsync();
    }

    /// <summary>Starts hosting when it was left on. Called once the window has loaded its settings.</summary>
    public Task StartAsync()
    {
        _started = true;
        return Enabled ? RestartAsync() : Task.CompletedTask;
    }

    /// <summary>Starts hosting, or stops it. This is the one button for it; the setting it changes is <see cref="Enabled"/>.</summary>
    [RelayCommand]
    private void ToggleHosting() => Enabled = !Enabled;

    /// <summary>The other half of <see cref="AllowNetwork"/>, for the choice between "this computer only" and "other computers on the network".</summary>
    public bool LocalOnly
    {
        get => !AllowNetwork;
        set { if (value) AllowNetwork = false; }
    }

    [RelayCommand]
    private void GeneratePassword() => Password = AuthThrottle.NewPassword();

    [RelayCommand]
    private void ClearPassword() => Password = "";

    [RelayCommand]
    private void CopyAddress()
    {
        try { System.Windows.Clipboard.SetText(SelectedPhoneAddress ?? Addresses()[0]); }
        catch (Exception error) { _reportError(Loc.T("Could not copy the address"), error.Message); }
    }

    /// <summary>Where this server's web page is, as a program on this computer reaches it.</summary>
    public string WebPageAddress => Addresses()[0];

    [RelayCommand]
    private void OpenWebPage()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(WebPageAddress) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        { _reportError(Loc.T("Could not open the web page"), error.Message); }
    }

    [RelayCommand]
    private void CopyFingerprint()
    {
        try { System.Windows.Clipboard.SetText(Fingerprint); }
        catch (Exception error) { _reportError(Loc.T("Could not copy the fingerprint"), error.Message); }
    }

    /// <summary>A new identity (certificate). Computers that trusted the old one will ask again; use it when the old one may have leaked.</summary>
    [RelayCommand]
    private async Task NewIdentityAsync()
    {
        ServerIdentity? old;
        await _gate.WaitAsync();
        try { old = _identity; _identity = ServerIdentity.Create(_identityFolder); Fingerprint = _identity.Readable; }
        finally { _gate.Release(); }
        if (_started && Enabled) await RestartAsync(); // the server stops using the old certificate before it is let go
        old?.Dispose();
    }

    /// <summary>Loads (or makes) the identity so that its fingerprint can be shown before hosting is switched on.</summary>
    public void LoadIdentity()
    {
        try { _identity ??= ServerIdentity.LoadOrCreate(_identityFolder); Fingerprint = _identity.Readable; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { _logger.LogWarning("The server identity could not be loaded: {ErrorType}", error.GetType().Name); }
    }

    private async Task RestartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_beacon is { } oldBeacon) { _beacon = null; await oldBeacon.DisposeAsync(); }
            if (_server is { } old) { _server = null; await old.DisposeAsync(); }
            if (!Enabled) { SetState(State.Off); return; }
            if (Port == 0) { SetState(State.BadPort); return; }
            _identity ??= ServerIdentity.LoadOrCreate(_identityFolder);
            Fingerprint = _identity.Readable;
            if (_service is null)
            {
                _service = _createService(this);
                _service.JobChangedByApi += (_, job) => JobChangedByApi?.Invoke(this, job);
                _service.JobDeletedByApi += (_, id) => JobDeletedByApi?.Invoke(this, id);
                await _service.StartAsync();
            }
            var address = AllowNetwork ? (Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any) : IPAddress.Loopback;
            // TLS always; plain HTTP only when the server can be reached from this computer alone (the simple way for local programs).
            var server = new LocalHttpServer(new HttpServerOptions(address, Port, Certificate: _identity.Certificate, AllowPlain: !AllowNetwork), _service.HandleAsync,
                error => _logger.LogError("An API request failed: {ErrorType}: {Message}", error.GetType().Name, error.Message));
            server.Start();
            _server = server;
            if (AllowNetwork) { _beacon = new BeaconSender(Announcement, BeaconPort); _beacon.Start(); }
            SetState(State.Listening);
        }
        catch (Exception error) when (error is SocketException or IOException or System.Security.Cryptography.CryptographicException)
        {
            SetState(State.Failed, error.Message);
            _reportError(Loc.T("The server could not start"), error.Message);
            _logger.LogWarning("The server could not start: {ErrorType}", error.GetType().Name);
        }
        finally { _gate.Release(); }
    }

    private ServerAnnouncement? Announcement() =>
        Enabled && _identity is not null && Port != 0
            ? new ServerAnnouncement(ServerId, DisplayName, Port, true, HasPassword, _edition, _version, _identity.Fingerprint)
            : null;

    private void SetState(State state, string failure = "")
    {
        _state = state; _failure = failure;
        _onUi(RefreshStatus);
    }

    /// <summary>
    /// The addresses clients can use: this computer, and (with the network on) each address of this computer on the network. Those on a network
    /// with a router come first: the others are usually virtual adapters (virtual machines, containers) that a phone or another computer cannot reach.
    /// </summary>
    public IReadOnlyList<string> Addresses()
    {
        var port = Port == 0 ? DefaultPort : Port;
        var scheme = AllowNetwork ? "https" : "http";
        var list = new List<string> { $"{scheme}://127.0.0.1:{port}" };
        if (!AllowNetwork) return list;
        var routed = new List<string>(); var other = new List<string>();
        try
        {
            foreach (var adapter in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(item => item.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && item.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback))
            {
                var properties = adapter.GetIPProperties();
                var hasRouter = properties.GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any));
                foreach (var unicast in properties.UnicastAddresses.Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(item.Address)))
                    (hasRouter && !IsSelfAssigned(unicast.Address) ? routed : other).Add($"https://{unicast.Address}:{port}");
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        list.AddRange(routed); list.AddRange(other);
        return list;
    }

    /// <summary>169.254.x.x: what Windows gives itself when no network handed it an address.</summary>
    private static bool IsSelfAssigned(IPAddress address) => address.GetAddressBytes() is [169, 254, ..];

    /// <summary>Where a phone (or another computer) on the same network reaches this server; null while it listens for this computer only. Read by the API from its own thread.</summary>
    public string? NetworkAddress => AllowNetwork && IsListening ? Addresses().Skip(1).FirstOrDefault() : null;

    /// <summary>The addresses of this server on the network, for the QR code; empty while it is off or listens for this computer only.</summary>
    public IReadOnlyList<string> PhoneAddresses { get; private set; } = [];
    public bool HasSeveralPhoneAddresses => PhoneAddresses.Count > 1;
    public bool HasPhoneCode => PhoneCode is not null;
    /// <summary>The server is on but only this computer can reach it, so a phone cannot open its web page.</summary>
    public bool ShowPhoneHint => IsListening && !AllowNetwork;

    private void RefreshPhone()
    {
        IReadOnlyList<string> addresses = IsListening && AllowNetwork ? Addresses().Skip(1).ToArray() : [];
        if (!addresses.SequenceEqual(PhoneAddresses))
        {
            PhoneAddresses = addresses;
            OnPropertyChanged(nameof(PhoneAddresses)); OnPropertyChanged(nameof(HasSeveralPhoneAddresses));
        }
        if (SelectedPhoneAddress is null || !addresses.Contains(SelectedPhoneAddress)) SelectedPhoneAddress = addresses.FirstOrDefault();
        OnPropertyChanged(nameof(ShowPhoneHint));
    }

    public void RefreshStatus()
    {
        var addresses = string.Join("   ", Addresses());
        Status = _state switch
        {
            State.Listening => (AllowNetwork ? Loc.T("Listening on this computer and on the network: {0}", addresses) : Loc.T("Listening on this computer only: {0}", addresses))
                + (IsBusy ? "\n" + Loc.T("Transcribing a recording that was sent to this server.") : ""),
            State.BadPort => Loc.T("The port must be a number from 1024 to 65535."),
            State.Failed => Loc.T("The server could not start: {0}", _failure),
            _ => Loc.T("The server is off.")
        };
        OnPropertyChanged(nameof(Example));
        RefreshPhone();
    }

    /// <summary>The commands for trying the server from a terminal. They are program text, not interface text, so they are not translated.</summary>
    public string Example
    {
        get
        {
            var address = Addresses()[0];
            var curl = AllowNetwork ? "curl.exe -k" : "curl.exe";
            var header = HasPassword ? $" -H \"Authorization: Bearer {Password}\"" : "";
            return $"{curl}{header} {address}/v1/health\n"
                + $"{curl} -X POST --data-binary @recording.mp3{header} \"{address}/v1/transcriptions?language=auto&name=recording.mp3\"\n"
                + $"{curl}{header} \"{address}/v1/transcriptions/ID?wait=60\"\n"
                + $"{curl}{header} \"{address}/v1/transcriptions/ID/transcript?format=txt\"\n"
                + $"{curl}{header} -F file=@recording.mp3 -F language=de {address}/v1/audio/transcriptions"
                + (HasPassword ? $"\n{curl}{header} -H \"Content-Type: application/json\" -d \"{{\\\"url\\\":\\\"https://example.com/talk.mp3\\\",\\\"language\\\":\\\"auto\\\"}}\" {address}/v1/links" : "");
        }
    }

    /// <summary>Stops listening without waiting; a recording being transcribed is cancelled with the app.</summary>
    public void StopForExit()
    {
        _started = false;
        if (_beacon is { } beacon) { _beacon = null; _ = beacon.DisposeAsync().AsTask(); }
        if (_server is { } server) { _server = null; _ = server.DisposeAsync().AsTask(); }
        if (_service is { } service) { _service = null; _ = service.DisposeAsync().AsTask(); }
    }

    public async ValueTask DisposeAsync()
    {
        _started = false;
        await _gate.WaitAsync();
        try
        {
            if (_beacon is { } beacon) { _beacon = null; await beacon.DisposeAsync(); }
            if (_server is { } server) { _server = null; await server.DisposeAsync(); }
            if (_service is { } service) { _service = null; await service.DisposeAsync(); }
            _identity?.Dispose(); _identity = null;
        }
        finally { _gate.Release(); }
    }
}
