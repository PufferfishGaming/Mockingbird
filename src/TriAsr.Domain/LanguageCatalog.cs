namespace TriAsr.Domain;

public sealed record LanguageOption(string Code, string Name)
{
    public string Display => Code is "auto" or "" ? Name : Name + " (" + Code + ")";
    public override string ToString() => Display;
    public bool DualEngine => LanguageCatalog.CanaryCodes.Contains(Code);
    public string Coverage => DualEngine ? "Whisper + Canary · independent comparison" : "Whisper · single-engine review";
}
public static class LanguageCatalog
{
    // Language identifiers from whisper.cpp g_lang; Canary coverage from the upstream Canary 1B v2 model card.
    public static IReadOnlySet<string> CanaryCodes { get; } = new HashSet<string>("bg hr cs da nl en et fi fr de el hu it lv lt mt pl pt ro sk sl es sv ru uk".Split(' '), StringComparer.Ordinal);
    public static IReadOnlyList<LanguageOption> All { get; } = new LanguageOption[]
    {
        new("en", "English"),
        new("zh", "Chinese"),
        new("de", "German"),
        new("es", "Spanish"),
        new("ru", "Russian"),
        new("ko", "Korean"),
        new("fr", "French"),
        new("ja", "Japanese"),
        new("pt", "Portuguese"),
        new("tr", "Turkish"),
        new("pl", "Polish"),
        new("ca", "Catalan"),
        new("nl", "Dutch"),
        new("ar", "Arabic"),
        new("sv", "Swedish"),
        new("it", "Italian"),
        new("id", "Indonesian"),
        new("hi", "Hindi"),
        new("fi", "Finnish"),
        new("vi", "Vietnamese"),
        new("he", "Hebrew"),
        new("uk", "Ukrainian"),
        new("el", "Greek"),
        new("ms", "Malay"),
        new("cs", "Czech"),
        new("ro", "Romanian"),
        new("da", "Danish"),
        new("hu", "Hungarian"),
        new("ta", "Tamil"),
        new("no", "Norwegian"),
        new("th", "Thai"),
        new("ur", "Urdu"),
        new("hr", "Croatian"),
        new("bg", "Bulgarian"),
        new("lt", "Lithuanian"),
        new("la", "Latin"),
        new("mi", "Maori"),
        new("ml", "Malayalam"),
        new("cy", "Welsh"),
        new("sk", "Slovak"),
        new("te", "Telugu"),
        new("fa", "Persian"),
        new("lv", "Latvian"),
        new("bn", "Bengali"),
        new("sr", "Serbian"),
        new("az", "Azerbaijani"),
        new("sl", "Slovenian"),
        new("kn", "Kannada"),
        new("et", "Estonian"),
        new("mk", "Macedonian"),
        new("br", "Breton"),
        new("eu", "Basque"),
        new("is", "Icelandic"),
        new("hy", "Armenian"),
        new("ne", "Nepali"),
        new("mn", "Mongolian"),
        new("bs", "Bosnian"),
        new("kk", "Kazakh"),
        new("sq", "Albanian"),
        new("sw", "Swahili"),
        new("gl", "Galician"),
        new("mr", "Marathi"),
        new("pa", "Punjabi"),
        new("si", "Sinhala"),
        new("km", "Khmer"),
        new("sn", "Shona"),
        new("yo", "Yoruba"),
        new("so", "Somali"),
        new("af", "Afrikaans"),
        new("oc", "Occitan"),
        new("ka", "Georgian"),
        new("be", "Belarusian"),
        new("tg", "Tajik"),
        new("sd", "Sindhi"),
        new("gu", "Gujarati"),
        new("am", "Amharic"),
        new("yi", "Yiddish"),
        new("lo", "Lao"),
        new("uz", "Uzbek"),
        new("fo", "Faroese"),
        new("ht", "Haitian creole"),
        new("ps", "Pashto"),
        new("tk", "Turkmen"),
        new("nn", "Nynorsk"),
        new("mt", "Maltese"),
        new("sa", "Sanskrit"),
        new("lb", "Luxembourgish"),
        new("my", "Burmese"),
        new("bo", "Tibetan"),
        new("tl", "Tagalog"),
        new("mg", "Malagasy"),
        new("as", "Assamese"),
        new("tt", "Tatar"),
        new("haw", "Hawaiian"),
        new("ln", "Lingala"),
        new("ha", "Hausa"),
        new("ba", "Bashkir"),
        new("jw", "Javanese"),
        new("su", "Sundanese"),
        new("yue", "Cantonese"),
    }.OrderBy(language => language.Name, StringComparer.Ordinal).ToArray();
    public static bool Supports(string code) => All.Any(language => language.Code == code);

    /// <summary>The most languages one choice can name: two, the case that was measured (a person who speaks English and Hungarian, say).</summary>
    public const int MaxChoice = 2;

    /// <summary>
    /// Reads a choice of languages: <c>auto</c> (or nothing), one language, or two joined with <c>+</c> (<c>en+hu</c>: the person speaks both and may switch).
    /// A space or a comma in place of the plus is read the same, because a plus in a web address turns into a space.
    /// </summary>
    /// <param name="codes">The languages named, in the order given; empty for <c>auto</c>.</param>
    /// <returns>False when a code is not a supported language, <c>auto</c> is named with another, or too many are named.</returns>
    public static bool TryParseChoice(string? value, out IReadOnlyList<string> codes)
    {
        codes = [];
        if (string.IsNullOrWhiteSpace(value)) return true;
        var parts = value.Split(['+', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(part => part.ToLowerInvariant()).Distinct().ToArray();
        if (parts is ["auto"]) return true;
        if (parts.Length == 0 || parts.Length > MaxChoice || parts.Any(part => !Supports(part))) return false;
        codes = parts;
        return true;
    }

    /// <summary>A choice written the one way it is saved and sent: <c>auto</c>, <c>en</c> or <c>en+hu</c>.</summary>
    public static string JoinChoice(IEnumerable<string?> codes)
    {
        var list = codes.Where(code => !string.IsNullOrWhiteSpace(code) && code != "auto").Select(code => code!).Distinct().Take(MaxChoice).ToArray();
        return list.Length == 0 ? "auto" : string.Join('+', list);
    }
}
