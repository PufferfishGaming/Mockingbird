using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TriAsr.Application;
using TriAsr.Infrastructure;

namespace TriAsr.App;

/// <summary>
/// The window of the Client edition: the servers on the network and the pages of the connected one. Nothing is transcribed on this computer:
/// there are no speech programs, no models and no hardware check here. The recording goes to a server, which runs the engines, and the transcript
/// comes back to be read, edited and exported.
/// </summary>
public sealed partial class ClientViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly ThemeManager _themes;
    private readonly ILogger<ClientViewModel> _logger;
    private readonly IStoragePaths _storage;
    private readonly UpdateService _updates;
    private readonly RemoteSession _session;
    private AppSettings _settings = new();
    private bool _initialized;
    private string _note = ""; // an English text that stays on the status line until the connection changes
    private object?[] _noteArguments = [];

    public ClientViewModel(SettingsStore store, ThemeManager themes, IStoragePaths storage, UpdateService updates, ILogger<ClientViewModel> logger)
    {
        _store = store; _themes = themes; _storage = storage; _updates = updates; _logger = logger;
        _session = new RemoteSession(storage.Root, (title, message) => RemoteSession.OnUi(() => ReportError(title, message)),
            () => SetNote("Edits saved with revision history"));
        _session.ConnectionChanged += _ => { _note = ""; _noteArguments = []; Refresh(); };
        _session.Remote.PropertyChanged += (_, change) => { if (change.PropertyName is nameof(RemoteWorkspaceViewModel.IsConnected) or nameof(RemoteWorkspaceViewModel.ServerName)) Refresh(); };
        Loc.Instance.PropertyChanged += (_, change) => { if (change.PropertyName == nameof(Loc.Version)) RemoteSession.OnUi(() => { Refresh(); RefreshUpdateTexts(); }); };
    }

    /// <summary>The servers on the network, and the connection to one of them.</summary>
    public ServerBrowserViewModel Servers => _session.Servers;

    /// <summary>The pages of the connected server (send, projects, review).</summary>
    public RemoteWorkspaceViewModel Remote => _session.Remote;

    /// <summary>The questions a connection asks (trust this fingerprint, enter the password). The window sets the real dialogs.</summary>
    public IServerDialogs? Dialogs { get => _session.Dialogs; set => _session.Dialogs = value; }

    /// <summary>Whether the right-hand panel with the servers is open.</summary>
    [ObservableProperty] private bool _showServerPanel = true;

    // ---- the status line and errors ---------------------------------------------------------------------------------------------------

    public string Status => _note.Length > 0 ? (_noteArguments.Length > 0 ? Loc.T(_note, _noteArguments) : Loc.T(_note)) : Remote.IsConnected ? Loc.T("Connected to {0}", Remote.ServerName) : Loc.T("Not connected to a server");

    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorTitle = Loc.Key("Something needs attention");
    [ObservableProperty] private string _errorMessage = "";

    public void ReportError(string title, string message)
    {
        HasError = false;
        ErrorTitle = Loc.Describe(title);
        ErrorMessage = Loc.Describe(message); // texts from the layers below are English and may have a translation
        HasError = true;
    }

    /// <summary>Whether the alert pulses to catch the eye (the same rule as in Studio).</summary>
    public bool PulseError => HasError && _settings.AnimateErrors && System.Windows.SystemParameters.ClientAreaAnimation && !System.Windows.SystemParameters.HighContrast;
    partial void OnHasErrorChanged(bool value) => OnPropertyChanged(nameof(PulseError));

    [RelayCommand] private void DismissError() => HasError = false;

    private void SetNote(string english, params object?[] arguments) { _note = english; _noteArguments = arguments; OnPropertyChanged(nameof(Status)); }
    private void Refresh() => OnPropertyChanged(nameof(Status));

    // ---- appearance and language ------------------------------------------------------------------------------------------------------

    public IReadOnlyList<string> Themes { get; } = ["System", "Light", "Dark"];
    [ObservableProperty] private string _selectedTheme = "System";
    public IReadOnlyList<UiLanguage> UiLanguages => Loc.Languages;
    [ObservableProperty] private string _language = Loc.English;

    /// <summary>False until the user has picked a language (the first start).</summary>
    public bool LanguageChosen { get; private set; }

    partial void OnSelectedThemeChanged(string value)
    {
        if (!_initialized) return;
        _themes.Apply(value);
        Persist();
    }

    partial void OnLanguageChanged(string value)
    {
        Loc.Instance.SetLanguage(value);
        if (_initialized) { LanguageChosen = true; Persist(); }
    }

    /// <summary>Called by the first-run language dialog: the choice is saved even if it is the language already showing.</summary>
    public void ChooseLanguage(string code)
    {
        if (!Loc.IsSupported(code)) code = Loc.English;
        LanguageChosen = true;
        if (Language == code) { Loc.Instance.SetLanguage(code); Persist(); }
        else Language = code;
    }

    public async Task InitializeAsync()
    {
        _settings = await _store.LoadAsync();
        SelectedTheme = _settings.Theme;
        LanguageChosen = Loc.IsSupported(_settings.Language) && _settings.Language.Length > 0;
        // TRIASR_LANGUAGE lets a test or screenshot run use a language without choosing it.
        var forced = Environment.GetEnvironmentVariable("TRIASR_LANGUAGE");
        Language = LanguageChosen ? _settings.Language : Loc.IsSupported(forced) ? forced! : Loc.English;
        Loc.Instance.SetLanguage(Language); // also when the value is unchanged: the table must match whatever an earlier host left behind
        if (_store.LastLoadError is not null) ReportError(Loc.T("Preferences could not be restored"), Loc.T("Defaults were loaded. {0}", _store.LastLoadError));
        RestoreUpdateSettings(_settings);
        ReadUpdateResult();
        _themes.Apply(SelectedTheme);
        Servers.Start();
        _initialized = true;
        Refresh();
    }

    private Task _saving = Task.CompletedTask;

    /// <summary>Saves the preferences in the background, one save after the other; each writes the state as it is when its turn comes, so the last one leaves the newest.</summary>
    private void Persist() => _saving = SaveAfterAsync(_saving);

    /// <summary>Completes when every save asked for so far has finished.</summary>
    public Task WhenSavedAsync() => _saving;

    private async Task SaveAfterAsync(Task previous)
    {
        await previous; // never faults: a failed save is reported below
        try
        {
            _settings = _settings with { Theme = SelectedTheme, Language = LanguageChosen ? Language : "", CheckForUpdates = AutoCheckUpdates, SkippedUpdateVersion = _skippedUpdateVersion, LastUpdateCheckUtc = _lastUpdateCheck };
            await _store.SaveAsync(_settings);
        }
        catch (Exception error)
        {
            ReportError(Loc.T("Preferences could not be saved"), Loc.T("Check access to the data folder."));
            _logger.LogWarning("Preference save failed: {ErrorType}", error.GetType().Name);
        }
    }

    /// <summary>Drops the connection and stops listening for servers (the window is closing).</summary>
    public void Close() => _session.Close();
}
