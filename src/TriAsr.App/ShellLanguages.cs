using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TriAsr.Domain;
using System.IO;
using System.Text.Json;

namespace TriAsr.App;
public sealed partial class ShellViewModel
{
    [ObservableProperty] private LanguageOption? _selectedExpansionLanguage = LanguageCatalog.All.First(language => language.Code == "fr");
    [ObservableProperty] private string _languageSearch = "";
    [ObservableProperty] private string _languageSetupStatus = Loc.Key("Choose a language and download its shared multilingual models. Existing models are reused; there is no duplicate download per language.");
    public IReadOnlyList<LanguageOption> ExpansionLanguages => LanguageText.InOrder(LanguageCatalog.All).Where(language => string.IsNullOrWhiteSpace(LanguageSearch)
        || language.Display.Contains(LanguageSearch, StringComparison.CurrentCultureIgnoreCase)
        || LanguageText.Of(language).Contains(LanguageSearch, StringComparison.CurrentCultureIgnoreCase)).ToArray();
    public string LanguageCoverage => SelectedExpansionLanguage is { } language ? T(language.Coverage) : T("Select a language");
    [ObservableProperty] private string _savedLanguageSummary = Loc.Key("No language setup shortcuts saved yet. Installed multilingual models still cover all their languages.");
    private async Task LoadLanguageExpansionsAsync()
    {
        var path = Path.Combine(storage.Root, "Config", "language-expansions.json");
        if (!File.Exists(path)) return;
        try
        {
            var codes = JsonSerializer.Deserialize<HashSet<string>>(await File.ReadAllTextAsync(path)) ?? [];
            _savedLanguageCodes = codes;
            ShowSavedLanguages();
        }
        catch (Exception error) { ReportError(T("Could not restore language setups"), error.Message); }
    }
    private HashSet<string> _savedLanguageCodes = [];
    private void ShowSavedLanguages()
    {
        if (_savedLanguageCodes.Count == 0) return;
        SavedLanguageSummary = T("Saved language setups: {0}", string.Join(", ", LanguageCatalog.All.Where(language => _savedLanguageCodes.Contains(language.Code)).Select(LanguageText.Of)));
    }
    public bool IsLanguagesPage => SelectedPage?.Name == "Languages";
    partial void OnLanguageSearchChanged(string value) => OnPropertyChanged(nameof(ExpansionLanguages));
    partial void OnSelectedExpansionLanguageChanged(LanguageOption? value) => OnPropertyChanged(nameof(LanguageCoverage));
    [RelayCommand]
    private async Task InstallLanguageSupportAsync()
    {
        if (SelectedExpansionLanguage is null || SetupBusy()) return;
        var language = SelectedExpansionLanguage;
        try
        {
            var needed = ModelCards.Where(card => card.Selected && (card.Entry.Family == "Whisper" || language.DualEngine)).ToArray();
            await DownloadCardsAsync(needed);
            if (needed.Any(card => !models.Inspect(card.Entry).Verified)) return;
            await PersistSelectionAsync();
            var path = Path.Combine(storage.Root, "Config", "language-expansions.json");
            var codes = File.Exists(path) ? JsonSerializer.Deserialize<HashSet<string>>(await File.ReadAllTextAsync(path)) ?? [] : [];
            codes.Add(language.Code);
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(codes)); File.Move(path + ".tmp", path, true);
            await LoadLanguageExpansionsAsync();
            SelectedLanguage = language.Code;
            LanguageSetupStatus = T("The selected language is ready for offline transcription: {0}. The shared Whisper model can auto-detect all 100 listed languages. {1}.", LanguageText.Of(language), T(language.Coverage));
            Status = T("Language support ready · {0}", LanguageText.Of(language));
        }
        catch (Exception error) { ReportError(T("Language setup failed"), error.Message); }
    }
    [RelayCommand] private void UseExpansionLanguage()
    {
        if (SelectedExpansionLanguage is null) return;
        SelectedLanguage = SelectedExpansionLanguage.Code;
        Status = T("Speech language: {0}", LanguageText.Of(SelectedExpansionLanguage));
    }
}
