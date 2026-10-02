using System.IO;
using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TriAsr.Domain;
using TriAsr.Infrastructure;

namespace TriAsr.App;

/// <summary>The network API (ADR-0013): switched on in Settings, listens only on this computer unless the network is allowed, and always needs the key.</summary>
public sealed partial class ShellViewModel
{
    public const int DefaultApiPort = 8642;

    [ObservableProperty] private bool _apiEnabled;
    [ObservableProperty] private bool _apiAllowNetwork;
    [ObservableProperty] private string _apiPortText = DefaultApiPort.ToString();
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private bool _showApiKey;
    [ObservableProperty] private string _apiStatus = Loc.Key("The API is off.");
    [ObservableProperty] private bool _isApiBusy;

    private enum ApiState { Off, Listening, BadPort, Failed }
    private ApiState _apiState;
    private string _apiFailure = "";
    private ApiService? _api;
    private LocalHttpServer? _apiServer;
    private readonly SemaphoreSlim _apiGate = new(1, 1);
    private bool _apiBatch;
    private Action<TranscriptionJob>? _updateJob;

    /// <summary>The port typed in Settings, or 0 when it is not a number from 1024 to 65535.</summary>
    public int ApiPort => int.TryParse(ApiPortText, out var port) && port is >= 1024 and <= 65535 ? port : 0;

    public bool HasApiKey => ApiKey.Length > 0;

    public string ApiKeyShown => ShowApiKey || ApiKey.Length < 8 ? ApiKey : ApiKey[..4] + new string('•', 20) + ApiKey[^4..];

    /// <summary>The commands for trying the API from a terminal. They are program text, not interface text, so they are not translated.</summary>
    public string ApiExample
    {
        get
        {
            var address = ApiService.Addresses(ApiPort == 0 ? DefaultApiPort : ApiPort, false)[0];
            var key = ApiKey.Length == 0 ? "YOUR_KEY" : ShowApiKey ? ApiKey : "YOUR_KEY";
            return $"curl.exe -H \"Authorization: Bearer {key}\" {address}/v1/health\n"
                + $"curl.exe -X POST --data-binary @recording.mp3 -H \"Authorization: Bearer {key}\" \"{address}/v1/transcriptions?language=auto&name=recording.mp3\"\n"
                + $"curl.exe -H \"Authorization: Bearer {key}\" \"{address}/v1/transcriptions/ID?wait=60\"\n"
                + $"curl.exe -H \"Authorization: Bearer {key}\" \"{address}/v1/transcriptions/ID/transcript?format=txt\"\n"
                + $"curl.exe -H \"Authorization: Bearer {key}\" -F file=@recording.mp3 -F language=de {address}/v1/audio/transcriptions";
        }
    }

    private void RestoreApiSettings(AppSettings settings)
    {
        _apiBatch = true;
        ApiPortText = settings.ApiPort.ToString();
        ApiKey = settings.ApiKey;
        ApiAllowNetwork = settings.ApiAllowNetwork;
        ApiEnabled = settings.ApiEnabled;
        _apiBatch = false;
    }

    partial void OnApiEnabledChanged(bool value)
    {
        if (value && ApiKey.Length == 0) ApiKey = AuthThrottle.NewKey();
        OnApiSettingChanged();
    }
    partial void OnApiAllowNetworkChanged(bool value) => OnApiSettingChanged();
    partial void OnApiPortTextChanged(string value) { OnPropertyChanged(nameof(ApiExample)); OnApiSettingChanged(); }
    partial void OnApiKeyChanged(string value) { OnPropertyChanged(nameof(HasApiKey)); OnPropertyChanged(nameof(ApiKeyShown)); OnPropertyChanged(nameof(ApiExample)); if (_initialized && !_apiBatch) Persist(); }
    partial void OnShowApiKeyChanged(bool value) { OnPropertyChanged(nameof(ApiKeyShown)); OnPropertyChanged(nameof(ApiExample)); }
    partial void OnIsApiBusyChanged(bool value) => RefreshApiStatus();

    private void OnApiSettingChanged()
    {
        if (!_initialized || _apiBatch) return;
        Persist();
        _ = RestartApiAsync();
    }

    [RelayCommand]
    private void NewApiKey() => ApiKey = AuthThrottle.NewKey();

    [RelayCommand]
    private void CopyApiKey()
    {
        if (ApiKey.Length == 0) return;
        try { System.Windows.Clipboard.SetText(ApiKey); Status = T("API key copied"); }
        catch (Exception error) { ReportError(T("Could not copy the API key"), error.Message); }
    }

    [RelayCommand]
    private void CopyApiAddress()
    {
        try { System.Windows.Clipboard.SetText(ApiService.Addresses(ApiPort == 0 ? DefaultApiPort : ApiPort, ApiAllowNetwork)[^1]); Status = T("API address copied"); }
        catch (Exception error) { ReportError(T("Could not copy the API address"), error.Message); }
    }

    /// <summary>Starts, restarts or stops the server to match the settings. The service behind it (and a recording it is working on) is kept while the server is off.</summary>
    private async Task RestartApiAsync()
    {
        await _apiGate.WaitAsync();
        try
        {
            if (_apiServer is { } old) { _apiServer = null; await old.DisposeAsync(); }
            if (!ApiEnabled) { SetApiState(ApiState.Off); return; }
            if (ApiPort == 0) { SetApiState(ApiState.BadPort); return; }
            if (ApiKey.Length == 0) ApiKey = AuthThrottle.NewKey();
            if (_api is null)
            {
                _api = CreateApiService();
                _api.JobChangedByApi += (_, job) => _updateJob?.Invoke(job);
                await _api.StartAsync();
            }
            var address = ApiAllowNetwork ? (Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any) : IPAddress.Loopback;
            var server = new LocalHttpServer(new HttpServerOptions(address, ApiPort), _api.HandleAsync,
                error => logger.LogError("An API request failed: {ErrorType}: {Message}", error.GetType().Name, error.Message));
            server.Start();
            _apiServer = server;
            SetApiState(ApiState.Listening);
        }
        catch (Exception error) when (error is SocketException or IOException)
        {
            SetApiState(ApiState.Failed, error.Message);
            ReportError(T("The API could not start"), error.Message);
            logger.LogWarning("The API server could not start: {ErrorType}", error.GetType().Name);
        }
        finally { _apiGate.Release(); }
    }

    private void SetApiState(ApiState state, string failure = "")
    {
        _apiState = state; _apiFailure = failure;
        OnUi(RefreshApiStatus);
    }

    private void RefreshApiStatus()
    {
        var addresses = string.Join("   ", ApiService.Addresses(ApiPort == 0 ? DefaultApiPort : ApiPort, ApiAllowNetwork));
        ApiStatus = _apiState switch
        {
            ApiState.Listening => (ApiAllowNetwork ? T("Listening on this computer and on the network: {0}", addresses) : T("Listening on this computer only: {0}", addresses))
                + (IsApiBusy ? "\n" + T("Transcribing a recording that was sent to the API.") : ""),
            ApiState.BadPort => T("The port must be a number from 1024 to 65535."),
            ApiState.Failed => T("The API could not start: {0}", _apiFailure),
            _ => T("The API is off.")
        };
    }

    private ApiService CreateApiService()
    {
        var incoming = Path.Combine(storage.Root, "Api", "Incoming");
        return new ApiService(new ApiServiceDependencies(queue, pipeline, repository,
            (id, token) => stages.LoadReviewAsync(id, token), incoming, Path.Combine(storage.Root, "Api", "Exports"), AppInfo.Version,
            () => ApiKey,
            language => { var missing = Array.Empty<string>(); OnUi(() => missing = MissingRequiredModelsFor(language)); return missing; },
            () => !IsModelBusy && !IsBenchmarking && !SetupRunning && !IsCheckingSystem,
            busy => OnUi(() => IsApiBusy = busy)));
    }

    private bool IsApiJob(TranscriptionJob job) =>
        job.SourcePath.StartsWith(Path.Combine(storage.Root, "Api", "Incoming") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Stops listening without waiting; a recording being transcribed is cancelled with the app.</summary>
    public void StopApiForExit()
    {
        if (_apiServer is { } server) { _apiServer = null; _ = server.DisposeAsync().AsTask(); }
        if (_api is { } service) { _api = null; _ = service.DisposeAsync().AsTask(); }
    }
}
