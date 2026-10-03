using TriAsr.Domain;

namespace TriAsr.App;

/// <summary>
/// The choice of the language that is spoken, as every page offers it: auto-detect, one language, or one language and a second one for speech that switches between
/// two (live dictation and notes: <see cref="Application.LiveLanguagePicker"/>; recordings: <see cref="Application.LanguageBlocks"/>). It is saved and sent as one value:
/// <c>auto</c>, <c>en</c> or <c>en+hu</c>.
/// </summary>
public static class SpeechLanguages
{
    public static readonly LanguageOption AutoDetect = new("auto", Loc.Key("Auto-detect language"));
    public static readonly LanguageOption NoSecond = new("", Loc.Key("No second language"));

    /// <summary>The first language list: auto-detect, then every language.</summary>
    public static IReadOnlyList<LanguageOption> First() => LanguageText.InOrder(LanguageCatalog.All).Prepend(AutoDetect).ToArray();

    /// <summary>The second language list: none, then every language but the first.</summary>
    public static IReadOnlyList<LanguageOption> Second(string first) => LanguageText.InOrder(LanguageCatalog.All.Where(language => language.Code != first)).Prepend(NoSecond).ToArray();

    /// <summary>The two languages of a saved value; the second is empty when there is none. A value that cannot be read is auto-detect.</summary>
    public static (string First, string Second) Split(string? saved) =>
        LanguageCatalog.TryParseChoice(saved, out var codes) ? (codes.Count > 0 ? codes[0] : "auto", codes.Count > 1 ? codes[1] : "") : ("auto", "");

    /// <summary>The value to save and send. Auto-detect takes no second language.</summary>
    public static string Join(string? first, string? second) => string.IsNullOrEmpty(first) || first == "auto" ? "auto" : LanguageCatalog.JoinChoice([first, second]);
}
