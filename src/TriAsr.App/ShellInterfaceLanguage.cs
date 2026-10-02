using CommunityToolkit.Mvvm.ComponentModel;

namespace TriAsr.App;

/// <summary>The language of the interface: chosen on the first start, changed in Settings, applied at once.</summary>
public sealed partial class ShellViewModel
{
    // The texts of the interface, looked up by their English wording (see Loc).
    private static string T(string text) => Loc.T(text);
    private static string T(string format, params object?[] args) => Loc.T(format, args);

    public IReadOnlyList<UiLanguage> UiLanguages => Loc.Languages;

    [ObservableProperty] private string _language = Loc.English;

    /// <summary>False until the user has picked a language (first start, or an installation from before translations existed).</summary>
    public bool LanguageChosen { get; private set; }

    private void RestoreLanguage(AppSettings settings)
    {
        LanguageChosen = Loc.IsSupported(settings.Language) && settings.Language.Length > 0;
        // TRIASR_LANGUAGE lets a test or screenshot run use a language without choosing it.
        var forced = Environment.GetEnvironmentVariable("TRIASR_LANGUAGE");
        Language = LanguageChosen ? settings.Language : Loc.IsSupported(forced) ? forced! : Loc.English;
        Loc.Instance.SetLanguage(Language); // also when the value is unchanged: the table must match whatever an earlier host left behind
    }

    /// <summary>Called by the first-run language dialog: the choice is saved even if it is the language already showing.</summary>
    public void ChooseLanguage(string code)
    {
        if (!Loc.IsSupported(code)) code = Loc.English;
        LanguageChosen = true;
        if (Language == code) { Loc.Instance.SetLanguage(code); Persist(); }
        else Language = code;
    }

    partial void OnLanguageChanged(string value)
    {
        Loc.Instance.SetLanguage(value);
        RefreshTranslatedTexts();
        if (_initialized) { LanguageChosen = true; Persist(); }
    }

    // ---- texts built earlier ------------------------------------------------------------------------------------------------------

    // Status lines that still say what they said when the program started are shown again in the new language.
    private readonly List<(Func<string> Get, Action<string> Set, string English)> _translatedDefaults = [];
    private string _shownLanguage = Loc.English;

    private void RegisterTranslatedDefault(Func<string> get, Action<string> set) => _translatedDefaults.Add((get, set, get()));

    private void RegisterTranslatedDefaults()
    {
        if (_translatedDefaults.Count > 0) return;
        RegisterTranslatedDefault(() => Status, value => Status = value);
        RegisterTranslatedDefault(() => Diagnostics, value => Diagnostics = value);
        RegisterTranslatedDefault(() => TranscriptionStage, value => TranscriptionStage = value);
        RegisterTranslatedDefault(() => ReviewSummary, value => ReviewSummary = value);
        RegisterTranslatedDefault(() => EngineStatus, value => EngineStatus = value);
        RegisterTranslatedDefault(() => ModelProgress, value => ModelProgress = value);
        RegisterTranslatedDefault(() => Recommendation, value => Recommendation = value);
        RegisterTranslatedDefault(() => SystemSummary, value => SystemSummary = value);
        RegisterTranslatedDefault(() => RecommendedDownloadSummary, value => RecommendedDownloadSummary = value);
        RegisterTranslatedDefault(() => BenchmarkSummary, value => BenchmarkSummary = value);
        RegisterTranslatedDefault(() => LanguageSetupStatus, value => LanguageSetupStatus = value);
        RegisterTranslatedDefault(() => SavedLanguageSummary, value => SavedLanguageSummary = value);
        RegisterTranslatedDefault(() => ActiveBackendSummary, value => ActiveBackendSummary = value);
        RegisterTranslatedDefault(() => ErrorTitle, value => ErrorTitle = value);
        RegisterTranslatedDefault(() => WatchStatus, value => WatchStatus = value);
        RegisterTranslatedDefault(() => TerminalStatus, value => TerminalStatus = value);
        RegisterTranslatedDefault(() => TerminalSendLabel, value => TerminalSendLabel = value);
    }

    /// <summary>
    /// The view model keeps many texts as finished strings. After a language change the ones that are only built from facts (the summaries, the
    /// readiness list, the banners...) are built again, and the ones that still say what they said at start-up are translated. A one-off status
    /// message that was shown earlier stays as it was until the next event replaces it.
    /// </summary>
    private void RefreshTranslatedTexts()
    {
        var previous = _shownLanguage; // the language the texts on screen were last built in (not necessarily the one before in the program: a test host starts afresh)
        _shownLanguage = Language;
        foreach (var (get, set, english) in _translatedDefaults)
            if (get() == Loc.TextIn(previous, english)) set(Loc.T(english));
        if (Hardware is { } profile && EngineStatus == _hardwareStatus) ShowHardwareStatus(profile);
        if (_rawCanaryNote is { } note) RawCanary = T(note);
        foreach (var region in Regions) region.NotifyLanguageChanged();
        _languagesInOrder = null; OnPropertyChanged(nameof(Languages)); // the names, and with them the alphabetical order, are those of the new language
        OnPropertyChanged(nameof(ThemeSummary)); OnPropertyChanged(nameof(StorageSummary)); OnPropertyChanged(nameof(LanguageCoverage)); OnPropertyChanged(nameof(ExpansionLanguages));
        RefreshReadiness();
        if (Hardware is not null) UpdateRecommendation();
        RefreshDownloadSummary();
        RefreshBenchmarkApplicability();
        RefreshResourceSummary();
        ShowReviewSummary(); ShowTranscriptionProgress(); ShowSavedLanguages();
        foreach (var card in ModelCards) card.NotifyLanguageChanged();
        if (!IsModelBusy) foreach (var card in ModelCards) card.Refresh(models.Inspect(card.Entry));
        _recorder?.RefreshTexts(); _dictation?.RefreshTexts(); _link?.RefreshTexts(); _linkHelper?.RefreshTexts(); RefreshWatchStatus(); Host.RefreshStatus(); RefreshSetupTexts(); RefreshUpdateTexts(); RefreshTerminal();
        if (Hardware is not null) _ = RefreshBackendsAsync();
    }
}
