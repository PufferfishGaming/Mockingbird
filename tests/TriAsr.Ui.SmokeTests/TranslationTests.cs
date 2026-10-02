using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using TriAsr.App;

namespace TriAsr.Ui.SmokeTests;

public sealed partial class TranslationTests
{
    private static readonly string[] Translated = Loc.Languages.Where(language => language.Code != Loc.English).Select(language => language.Code).ToArray();

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void TheInterfaceIsOfferedInTheFiveLanguages()
    {
        Assert.Equal(["en", "hu", "de", "es", "fr"], Loc.Languages.Select(language => language.Code));
        Assert.Equal(["English", "Magyar", "Deutsch", "Español", "Français"], Loc.Languages.Select(language => language.NativeName));
    }

    [Theory]
    [InlineData("hu")] [InlineData("de")] [InlineData("es")] [InlineData("fr")]
    public void EveryTextTheAppUsesHasATranslation(string code)
    {
        var table = Loc.Load(code);
        var problems = new List<string>();
        foreach (var text in TranslationSources.UsedTexts())
        {
            if (!table.TryGetValue(text, out var translated) || string.IsNullOrWhiteSpace(translated)) { problems.Add("missing: " + Shorten(text)); continue; }
            if (!TranslationSources.Placeholders(text).SequenceEqual(TranslationSources.Placeholders(translated))) problems.Add("placeholders differ: " + Shorten(text));
            if (text.Count(c => c == '\n') != translated.Count(c => c == '\n')) problems.Add("line breaks differ: " + Shorten(text));
            if (TranslationSources.Placeholders(text).Length > 0)
                try { string.Format(System.Globalization.CultureInfo.InvariantCulture, translated, 1, 2, 3, 4, 5, 6, 7); }
                catch (FormatException) { problems.Add("not a valid format string (stray brace?): " + Shorten(translated)); }
        }
        Assert.True(problems.Count == 0, $"{code}: {problems.Count} problems\n" + string.Join("\n", problems.Take(25)));
    }

    [Theory]
    [InlineData("hu")] [InlineData("de")] [InlineData("es")] [InlineData("fr")]
    public void ATranslationTableHoldsNothingTheAppNoLongerUses(string code)
    {
        var used = TranslationSources.UsedTexts();
        var stale = Loc.Load(code).Keys.Where(key => !used.Contains(key)).Select(Shorten).ToArray();
        Assert.True(stale.Length == 0, $"{code}: {stale.Length} unused entries\n" + string.Join("\n", stale.Take(25)));
    }

    [Fact]
    public void NoXamlTextIsLeftUntranslated()
    {
        var literal = new List<string>();
        var allowed = NumberOrUnit();
        foreach (var path in TranslationSources.XamlFiles())
            foreach (var element in XDocument.Load(path).Descendants())
                foreach (var attribute in element.Attributes())
                {
                    var name = attribute.Name.LocalName;
                    if (name is not ("Text" or "Content" or "Header" or "ToolTip" or "Title" or "AutomationProperties.Name" or "AutomationProperties.HelpText")) continue;
                    var value = attribute.Value;
                    if (value.StartsWith('{') || !value.Any(char.IsLetter) || allowed.IsMatch(value) || value == "Mockingbird Studio") continue;
                    literal.Add($"{Path.GetFileName(path)}: {name}=\"{Shorten(value)}\"");
                }
        Assert.True(literal.Count == 0, "Text written straight into XAML (use {local:T '...'}):\n" + string.Join("\n", literal));
    }

    [GeneratedRegex(@"(?<![\w])(?:Loc\.)?(?:T|Key)\(")]
    private static partial Regex LookupCall();

    [GeneratedRegex(@"^[+\-\u2212]?[\d.,]+\s*(\u00D7|s)?$")]
    private static partial Regex NumberOrUnit();

    [Fact]
    public void NoMessageIsShownWithoutBeingLookedUp()
    {
        // Text that reaches the screen goes through T(...) / Loc.T(...) / Loc.Key(...). These are the places where a plain string would slip through.
        var properties = "Status|ModelProgress|BackendProgress|BenchmarkProgress|BenchmarkSummary|SetupTitle|SetupDetail|UpdateTitle|UpdateDetail|UpdateStatusText|WatchStatus|TerminalStatus|ReviewSummary|EngineStatus|Recommendation|SystemSummary|Diagnostics|ErrorTitle|ErrorMessage|LanguageSetupStatus|SavedLanguageSummary|ActiveBackendSummary|RawCanary|RawWhisper|Title";
        var plainAssignment = new Regex($@"\b({properties})\s*\+?=\s*\$?""(?=[^""]*[A-Za-z])", RegexOptions.Compiled);
        var plainError = new Regex(@"ReportError\(\s*(\$?""|[A-Za-z_.]+\s*\+)", RegexOptions.Compiled);
        var problems = new List<string>();
        foreach (var path in TranslationSources.ShownTextFiles())
        {
            var number = 0;
            foreach (var line in File.ReadLines(path))
            {
                number++;
                var code = line.Split("//")[0];
                if (LookupCall().IsMatch(code)) continue;
                if (plainAssignment.IsMatch(code) || plainError.IsMatch(code)) problems.Add($"{Path.GetFileName(path)}:{number}: {Shorten(code.Trim(), 110)}");
            }
        }
        Assert.True(problems.Count == 0, "Messages shown without a lookup:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void AnUntranslatedTextIsShownInEnglishAndNeverBlank()
    {
        var loc = new Loc();
        loc.SetLanguage("de");
        Assert.Equal("This sentence is not in any table.", loc.Translate("This sentence is not in any table."));
        Assert.Equal("", loc.Translate(""));
        Assert.NotEqual("Cancel", loc.Translate("Cancel"));
        loc.SetLanguage("xx"); // an unknown language falls back to English
        Assert.Equal("en", loc.Language);
        Assert.Equal("Cancel", loc.Translate("Cancel"));
    }

    [Fact]
    public void SwitchingTheLanguageTellsTheBindingsAndRemembersTheOneBefore()
    {
        var loc = new Loc();
        var changes = new List<string?>();
        loc.PropertyChanged += (_, change) => changes.Add(change.PropertyName);
        loc.SetLanguage("fr");
        loc.SetLanguage("hu");
        Assert.Equal("fr", loc.Previous);
        Assert.Equal("hu", loc.Language);
        Assert.Equal(2, loc.Version);
        Assert.Contains(nameof(Loc.Version), changes);
        Assert.Equal(Loc.Load("fr")["Cancel"], Loc.TextIn("fr", "Cancel"));
        Assert.Equal("Cancel", Loc.TextIn("en", "Cancel"));
    }

    [Fact]
    public void NumbersAndNamesInTheArgumentsAreFilledInAsTheyAre()
    {
        var before = Loc.Instance.Language;
        try
        {
            Loc.Instance.SetLanguage("de");
            var text = Loc.T("{0}: {1}", Loc.T("Cancel"), "C:\\Users\\Anna\\Recordings"); // a label is translated by the caller, a path is left alone
            Assert.StartsWith(Loc.Load("de")["Cancel"] + ": ", text);
            Assert.EndsWith("C:\\Users\\Anna\\Recordings", text);
            Assert.StartsWith("Cancel: ", Loc.T("{0}: {1}", "Cancel", "x")); // an argument that looks like a label is still a fact
        }
        finally { Loc.Instance.SetLanguage(before); }
    }

    [Fact]
    public void ThePrivacyPolicyIsAvailableInEveryLanguageWithTheSameStructure()
    {
        var english = Loc.PrivacyPolicy("en");
        var headings = english.Split('\n').Count(line => line.StartsWith('#'));
        foreach (var code in Translated)
        {
            var policy = Loc.PrivacyPolicy(code);
            Assert.NotEqual(english, policy);
            Assert.Equal(headings, policy.Split('\n').Count(line => line.StartsWith('#')));
            Assert.Contains("https://huggingface.co/privacy", policy);
            Assert.Contains("https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement", policy);
            Assert.Contains("127.0.0.1", policy);
        }
    }

    [Fact]
    public async Task TheLanguageIsAskedOnceChosenInSettingsAndRememberedAcrossRestarts()
    {
        var root = NewRoot();
        var before = Loc.Instance.Language;
        try
        {
            using (var host = App.App.CreateHost(root))
            {
                var shell = host.Services.GetRequiredService<ShellViewModel>();
                await shell.InitializeAsync();
                Assert.False(shell.LanguageChosen);                 // a fresh install has not been asked yet
                Assert.Equal("en", shell.Language);
                shell.ChooseLanguage("hu");
                Assert.True(shell.LanguageChosen);
                Assert.Equal("hu", Loc.Instance.Language);
                Assert.Equal(Loc.Load("hu")["Cancel"], Loc.T("Cancel"));
                Assert.StartsWith(Loc.Load("hu")["Projects and logs: {0}\nModels: {1}"].Split("{0}")[0], shell.StorageSummary);
                var store = host.Services.GetRequiredService<SettingsStore>();
                await WaitForAsync(async () => (await store.LoadAsync()).Language == "hu");
                shell.Language = "de";                              // changing it in Settings
                Assert.Equal("de", Loc.Instance.Language);
                await WaitForAsync(async () => (await store.LoadAsync()).Language == "de");
                await WaitForAsync(() => Task.FromResult(shell.Status == Loc.Load("de")["Preferences saved locally"]));
            }
            using (var host = App.App.CreateHost(root))
            {
                var shell = host.Services.GetRequiredService<ShellViewModel>();
                await shell.InitializeAsync();
                Assert.True(shell.LanguageChosen);
                Assert.Equal("de", shell.Language);
                Assert.Equal("de", Loc.Instance.Language);
                Assert.Equal(Loc.Load("de")["Workspace ready · no active job"], shell.Status);   // a status that is still the start-up text appears in the language
            }
        }
        finally { Loc.Instance.SetLanguage(before); TestCleanup.Delete(root); }
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline) { if (await condition()) return; await Task.Delay(50); }
        throw new TimeoutException("The expected state was not reached.");
    }

    private static string Shorten(string text, int length = 90) => (text.Length <= length ? text : text[..length] + "…").Replace("\n", "\\n");
}
