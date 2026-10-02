using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace TriAsr.App;

/// <summary>A language the interface is available in. The name is written in that language, as language pickers do.</summary>
public sealed record UiLanguage(string Code, string NativeName);

/// <summary>
/// The interface language. Texts are looked up by their English wording: English needs no table, and every other language has a JSON table
/// (Languages/xx.json, embedded) mapping the English text to its translation. A text without a translation is shown in English, never blank;
/// tests make sure no text used by the app is missing from any table.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public const string English = "en";

    public static Loc Instance { get; } = new();

    /// <summary>The languages of the interface, English first.</summary>
    public static IReadOnlyList<UiLanguage> Languages { get; } =
        [new("en", "English"), new("hu", "Magyar"), new("de", "Deutsch"), new("es", "Español"), new("fr", "Français")];

    private IReadOnlyDictionary<string, string> _table = new Dictionary<string, string>();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Language { get; private set; } = English;

    /// <summary>The language before the last change.</summary>
    public string Previous { get; private set; } = English;

    /// <summary>Changes whenever the language does; bindings listen to it to refresh their texts.</summary>
    public int Version { get; private set; }

    public static bool IsSupported(string? code) => Languages.Any(language => language.Code == code);

    /// <summary>The interface language that matches the language Windows is set to, or English.</summary>
    public static string Detect(CultureInfo? culture = null)
    {
        var code = (culture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName;
        return IsSupported(code) ? code : English;
    }

    public string Translate(string text)
    {
        if (text.Length == 0) return text;
        return _table.TryGetValue(text, out var translated) && translated.Length > 0 ? translated : text;
    }

    public static string T(string text) => Instance.Translate(text);

    /// <summary>
    /// Translates a format string and fills it in with the user's regional number and date formats. The arguments are facts and are put in as they
    /// are: a file name, a path or a program's output is never looked up. An argument that is itself a text to translate (a name from a list) is
    /// passed through <c>T(...)</c> by the caller, and a message from a layer below the app through <see cref="Describe"/>.
    /// </summary>
    public static string T(string format, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Instance.Translate(format), args);

    /// <summary>
    /// A message that came from a layer below the app. A fixed sentence (listed in extra-keys.json) is translated as it stands; a sentence with a
    /// number or a name inside is matched with its template (<see cref="LowerLayerMessages"/>) and translated with the same facts. Anything else,
    /// such as the output of a program, comes back as it was written.
    /// </summary>
    public static string Describe(string message)
    {
        if (message.Length == 0 || Instance.Language == English) return message;
        var translated = Instance.Translate(message);
        return translated != message ? translated : LowerLayerMessages.Translate(message) ?? message;
    }

    /// <summary>Marks a text that is shown later (in a list of choices, say) so that it is translated and checked like the others.</summary>
    public static string Key(string text) => text;

    public void SetLanguage(string code)
    {
        if (!IsSupported(code)) code = English;
        _table = code == English ? new Dictionary<string, string>() : Load(code);
        Previous = Language;
        Language = code;
        Version++;
        PropertyChanged?.Invoke(this, new(nameof(Language)));
        PropertyChanged?.Invoke(this, new(nameof(Version)));
    }

    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Tables = [];

    /// <summary>A text in a given language, whatever the interface language is now (used to tell whether a shown text is still an untouched default).</summary>
    public static string TextIn(string code, string text)
    {
        if (code == English) return text;
        IReadOnlyDictionary<string, string> table;
        lock (Tables) { if (!Tables.TryGetValue(code, out table!)) Tables[code] = table = Load(code); }
        return table.TryGetValue(text, out var translated) && translated.Length > 0 ? translated : text;
    }

    /// <summary>The translation table of a language, from the file embedded in the app.</summary>
    public static IReadOnlyDictionary<string, string> Load(string code)
    {
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"Mockingbird.Lang.{code}.json");
        if (stream is null) return new Dictionary<string, string>();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
    }

    /// <summary>The privacy policy in the interface language, or the English one when it has not been translated.</summary>
    public static string PrivacyPolicy(string code)
    {
        var assembly = typeof(Loc).Assembly;
        using var stream = (code != English ? assembly.GetManifestResourceStream($"Mockingbird.Privacy.{code}.md") : null) ?? assembly.GetManifestResourceStream("Mockingbird.Privacy.md")
            ?? throw new InvalidOperationException("The privacy policy resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
