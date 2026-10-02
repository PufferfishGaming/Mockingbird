using System.Globalization;
using System.Text.RegularExpressions;

namespace TriAsr.App;

/// <summary>
/// Sentences from the layers below the app that have a number, a name or a program's output in the middle. The layers cannot translate (they know
/// nothing of the interface), so the app recognises the sentence by its template and says it again in the interface language with the same facts.
/// Every template is written exactly as the layer writes it, with {0}, {1} where the facts go; a test checks that each one still appears in the
/// source of a lower layer. Fixed sentences without facts are in Languages/extra-keys.json instead.
/// </summary>
public static partial class LowerLayerMessages
{
    public static readonly IReadOnlyList<string> Templates =
    [
        Loc.Key("Whisper stopped with exit code {0}. See the saved engine output."),
        Loc.Key("Whisper was set to use {0}, but used {1}."),
        Loc.Key("The processing task timed out after {0} seconds."),
        Loc.Key("Correction worker did not verify the requested GPU backend. {0}"),
        Loc.Key("FFmpeg exited with code {0}: {1}"),
        Loc.Key("The Windows device query exited with code {0}."),
        Loc.Key("Speech detection failed. {0}"),
        Loc.Key("Requested {0}, actual backend is {1} ({2})."),
        Loc.Key("Canary ABI version mismatch: {0}"),
        Loc.Key("llama.cpp exited {0}: {1}"),
        Loc.Key("The microphone could not be opened (Windows error {0}). Check that it is connected and not in use by another program.")
    ];

    private static readonly (Regex Pattern, string Template)[] Patterns = Templates.Select(template => (Pattern(template), template)).ToArray();

    /// <summary>The message in the interface language, or null when it is not one of the templates.</summary>
    public static string? Translate(string message)
    {
        foreach (var (pattern, template) in Patterns)
        {
            if (pattern.Match(message) is not { Success: true } match) continue;
            var facts = Enumerable.Range(0, Placeholder().Matches(template).Select(item => item.Groups[1].Value).Distinct().Count())
                .Select(index => (object)match.Groups["a" + index.ToString(CultureInfo.InvariantCulture)].Value).ToArray();
            return string.Format(CultureInfo.CurrentCulture, Loc.T(template), facts);
        }
        return null;
    }

    private static Regex Pattern(string template) =>
        new("^" + Placeholder().Replace(Regex.Escape(template), item => $"(?<a{item.Groups[1].Value}>.*?)") + "$", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    [GeneratedRegex(@"\\?\{(\d+)\}")]
    private static partial Regex Placeholder();
}
