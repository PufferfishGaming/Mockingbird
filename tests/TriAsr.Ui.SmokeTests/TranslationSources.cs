using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TriAsr.Ui.SmokeTests;

/// <summary>Reads the source files of the app to find every text the interface can show, so that the tests can compare it with the translation tables.</summary>
internal static partial class TranslationSources
{
    // Windows that show their text in every language at once, and a developer-only window that is never shown.
    private static readonly string[] LiteralWindows = ["LanguageChoiceWindow.xaml", "BootstrapWindow.xaml"];
    // Smoke-test harness and helpers that never show text to the user.
    private static readonly string[] HarnessFiles = ["ShellSmoke.cs", "TestSpeech.cs"];
    // Files whose texts are looked up but which also hold the smoke-test code that writes plain strings into view-model properties.
    private static readonly string[] SmokeCodeFiles = ["App.xaml.cs"];

    public static string AppFolder { get; } = Path.Combine(RepositoryRoot(), "src", "TriAsr.App");

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TriAsr.slnx"))) return directory.FullName;
        throw new InvalidOperationException("The repository root was not found.");
    }

    // Folders the build writes into: generated copies of the sources, never shown to anyone.
    private static bool IsBuildOutput(string path) => path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar);

    /// <summary>Every XAML file of the app, in nested folders too (themes, controls).</summary>
    public static IEnumerable<string> XamlFiles(bool includeLiteralWindows = false) =>
        Directory.EnumerateFiles(AppFolder, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path) && (includeLiteralWindows || !LiteralWindows.Contains(Path.GetFileName(path))));

    public static IEnumerable<string> CodeFiles(bool includeHarness = false) =>
        Directory.EnumerateFiles(AppFolder, "*.cs").Where(path => includeHarness || !HarnessFiles.Contains(Path.GetFileName(path)));

    /// <summary>The source of the layers below the app, where the messages listed in extra-keys.json and in <c>LowerLayerMessages</c> are written.</summary>
    public static string LowerLayerSource() =>
        string.Join('\n', Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path) && !path.StartsWith(AppFolder + Path.DirectorySeparatorChar)).Select(File.ReadAllText));

    /// <summary>
    /// Properties that XAML shows as they are. Each one holds either a text the view model builds in the interface language (and builds again
    /// after a language change), the user's own words or a path, or a value that is not words. Showing any other bound property directly is how
    /// an untranslated message slips through, so a new one has to be added here on purpose or shown through <c>{local:Tr ...}</c>.
    /// </summary>
    public static readonly IReadOnlySet<string> ShownAsTheyAre = new HashSet<string>
    {
        // built by the view model in the interface language, rebuilt after a language change
        "ErrorTitle", "ErrorMessage", "UpdateTitle", "UpdateDetail", "UpdateStatusText", "SetupTitle", "SetupDetail", "TranscriptionStage", "TranscriptionProgressSummary",
        "TerminalStatus", "Readiness", "ModelProgress", "WatchStatus", "ReviewSummary", "SystemSummary", "Recommendation", "RecommendedDownloadSummary",
        "BenchmarkProgress", "BenchmarkSummary", "SavedLanguageSummary", "LanguageCoverage", "LanguageSetupStatus", "BackendProgress", "ActiveBackendSummary",
        "ThemeSummary", "ResourceSummary", "StorageSummary", "EngineStatus", "Status", "Title", "Details", "SetupStartLabel", "SetupDismissLabel", "TerminalSendLabel", "ApiStatus",
        // a review region's texts, announced again by ReviewRegion.NotifyLanguageChanged
        "Time", "SelectedRegion.Evidence", "SelectedRegion.CanaryHeading",
        // the user's own words, program output, paths and numbers
        "Text", "SelectedRegion.Text", "SelectedRegion.Whisper", "SelectedRegion.Canary", "RawWhisper", "RawCanary", "SearchText", "LanguageSearch", "SourcePath", "Location",
        "TerminalOutput", "TerminalDirectory", "TerminalInput", "Diagnostics", "Model", "ThreadLabel", "MedianSeconds", "RealTimeFactor", "Backend", "ApiPortText", "ApiKeyShown", "ApiExample",
        // the name of a language in that language
        "NativeName"
    };

    /// <summary>The code files whose assignments and error reports must all go through a lookup.</summary>
    public static IEnumerable<string> ShownTextFiles() => CodeFiles().Where(path => !SmokeCodeFiles.Contains(Path.GetFileName(path)));

    /// <summary>Every English text that is looked up: {local:T '...'} in XAML, T("..."), Loc.T("...") and Loc.Key("...") in code, and Languages/extra-keys.json.</summary>
    public static SortedSet<string> UsedTexts()
    {
        var texts = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in XamlFiles())
            foreach (var attribute in XDocument.Load(path).Descendants().SelectMany(element => element.Attributes()))
                if (MarkupText().Match(attribute.Value) is { Success: true } match) texts.Add(Regex.Replace(match.Groups[1].Value, @"\\(.)", "$1"));
        foreach (var path in CodeFiles())
            foreach (Match match in CodeText().Matches(File.ReadAllText(path))) texts.Add(Unescape(match.Groups[1].Value));
        foreach (var text in JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(AppFolder, "Languages", "extra-keys.json")))!) texts.Add(text);
        return texts;
    }

    [GeneratedRegex(@"^\{local:T '((?:[^'\\]|\\.)*)'\}$", RegexOptions.Singleline)]
    private static partial Regex MarkupText();

    [GeneratedRegex(@"(?<![\w.])(?:Loc\.)?(?:T|Key)\(\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex CodeText();

    /// <summary>The characters a C# string literal spells with a backslash.</summary>
    public static string Unescape(string literal) => Regex.Replace(literal, @"\\(u[0-9a-fA-F]{4}|.)", match => match.Groups[1].Value switch
    {
        "n" => "\n", "t" => "\t", "r" => "\r",
        var unicode when unicode.Length == 5 && unicode[0] == 'u' => ((char)Convert.ToInt32(unicode[1..], 16)).ToString(),
        var other => other
    });

    /// <summary>The numbered placeholders of a format string, with their format specifiers, in sorted order.</summary>
    public static string[] Placeholders(string text) => PlaceholderPattern().Matches(text).Select(match => match.Value).Order(StringComparer.Ordinal).ToArray();

    [GeneratedRegex(@"\{\d+(?::[^}]*)?\}")]
    private static partial Regex PlaceholderPattern();
}
