using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using TriAsr.App;
using TriAsr.Domain;

namespace TriAsr.Ui.SmokeTests;

/// <summary>What a review of the translations found, kept as tests so it stays fixed: choices, checkpoints, open review texts, errors, counts and units.</summary>
public sealed partial class TranslationTests
{
    private static T InLanguage<T>(string code, Func<T> read)
    {
        var before = Loc.Instance.Language;
        try { Loc.Instance.SetLanguage(code); return read(); }
        finally { Loc.Instance.SetLanguage(before); }
    }

    [Fact]
    public void EveryChoiceTheViewModelOffersAsTextIsInTheTables()
    {
        var root = NewRoot();
        try
        {
            using var host = App.App.CreateHost(root);
            var shell = host.Services.GetRequiredService<ShellViewModel>();
            var choices = typeof(ShellViewModel).GetProperties()
                .Where(property => typeof(IEnumerable<string>).IsAssignableFrom(property.PropertyType) && property.PropertyType != typeof(string))
                .SelectMany(property => ((IEnumerable<string>)property.GetValue(shell)!).Select(value => (property.Name, value))).ToArray();
            Assert.Contains(choices, choice => choice.value == ShellViewModel.WatchProjectOnly); // the lists are really found
            var problems = new List<string>();
            foreach (var code in Translated)
            {
                var table = Loc.Load(code);
                foreach (var (name, value) in choices)
                    if (value != "PowerShell" && !table.ContainsKey(value)) // PowerShell is a product name
                        problems.Add($"{code}: {name} offers \"{value}\", which is in no table");
            }
            foreach (var language in LanguageCatalog.All)
                foreach (var code in Translated)
                    if (!Loc.Load(code).ContainsKey(language.Name)) problems.Add($"{code}: language name \"{language.Name}\"");
            Assert.True(problems.Count == 0, string.Join("\n", problems.Take(25)));
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public void AJobsCheckpointAndTheWayARegionCameAboutAreWordsInEveryLanguage()
    {
        Assert.Equal("Audio prepared", InLanguage("en", () => JobText.Checkpoint("normalized")));
        Assert.Equal("chosen by the correction model", InLanguage("en", () => SourceText.Of("llm-arbitrated")));
        Assert.Equal("one engine · needs listening", InLanguage("en", () => SourceText.Of("single-asr-needs-listening")));
        foreach (var code in Translated)
        {
            Assert.NotEqual("Audio prepared", InLanguage(code, () => JobText.Checkpoint("normalized")));
            Assert.NotEqual("chosen by the correction model", InLanguage(code, () => SourceText.Of("llm-arbitrated")));
            Assert.NotEqual("one engine · needs listening", InLanguage(code, () => SourceText.Of("single-asr-needs-listening")));
            Assert.NotEqual(InLanguage("en", () => SourceText.Of("agreement")), InLanguage(code, () => SourceText.Of("agreement")));
        }
    }

    [Fact]
    public void TheTextsOfARegionAlreadyOpenAreAnnouncedAgainWhenTheLanguageChanges()
    {
        var root = NewRoot(); var before = Loc.Instance.Language;
        try
        {
            using var host = App.App.CreateHost(root);
            var shell = host.Services.GetRequiredService<ShellViewModel>();
            Loc.Instance.SetLanguage("en");
            var region = new ReviewRegion(new FinalRegion(0, 1000, "text", "whisper", "canary", "uncertain", null, 0.5, null, false));
            shell.Regions.Add(region);
            var shown = (Time: region.Time, Heading: region.CanaryHeading, Evidence: region.Evidence);
            var announced = new List<string?>();
            region.PropertyChanged += (_, change) => announced.Add(change.PropertyName);
            shell.Language = "fr";
            Assert.Contains(nameof(ReviewRegion.Time), announced);
            Assert.Contains(nameof(ReviewRegion.CanaryHeading), announced);
            Assert.Contains(nameof(ReviewRegion.Evidence), announced);
            Assert.NotEqual(shown.Time, region.Time);
            Assert.NotEqual(shown.Heading, region.CanaryHeading);
            Assert.NotEqual(shown.Evidence, region.Evidence);
        }
        finally { Loc.Instance.SetLanguage(before); TestCleanup.Delete(root); }
    }

    [Fact]
    public void ASavedErrorThatIsAKnownSentenceIsShownInTheLanguage()
    {
        const string interrupted = "Previous run was interrupted. Resume to reuse completed stages.";
        foreach (var code in Translated)
        {
            Assert.Equal(Loc.Load(code)[interrupted], InLanguage(code, () => TranslateConverter.Text(interrupted)));
            Assert.Equal("The download host is unreachable.", InLanguage(code, () => TranslateConverter.Text("The download host is unreachable.")));
        }
        // The two places that show a saved error use the translating binding, not the plain one.
        foreach (var path in TranslationSources.XamlFiles())
            Assert.DoesNotContain("{Binding Error}", File.ReadAllText(path));
    }

    [Fact]
    public void AMessageFromALowerLayerWithFactsInItIsTranslatedWithTheSameFacts()
    {
        Assert.NotEmpty(LowerLayerMessages.Templates);
        var source = TranslationSources.LowerLayerSource();
        foreach (var template in LowerLayerMessages.Templates)
        {
            // The template is written as the lower layer writes it: every piece of it is still in a lower layer's source.
            foreach (var piece in Regex.Split(template, @"\{\d+\}").Where(piece => piece.Trim().Length >= 6))
                Assert.True(source.Contains(piece), $"\"{piece}\" of \"{template}\" is not in the source of any lower layer any more");
            var facts = Enumerable.Range(0, TranslationSources.Placeholders(template).Length).Select(index => "<fact " + index + ">").ToArray();
            var message = string.Format(CultureInfo.InvariantCulture, template, facts.Cast<object>().ToArray());
            Assert.Equal(message, InLanguage("en", () => Loc.Describe(message)));
            foreach (var code in Translated)
            {
                var shown = InLanguage(code, () => Loc.Describe(message));
                Assert.NotEqual(message, shown);
                foreach (var fact in facts) Assert.Contains(fact, shown);
            }
        }
        Assert.Equal("Whisper exploded for reasons of its own.", InLanguage("de", () => Loc.Describe("Whisper exploded for reasons of its own.")));
    }

    [Fact]
    public void TheSentencesInExtraKeysThatCameFromALowerLayerAreStillThere()
    {
        var source = TranslationSources.LowerLayerSource();
        var gone = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(TranslationSources.AppFolder, "Languages", "extra-keys.json")))!
            .Where(text => text.Length >= 25 && text.EndsWith('.') && !source.Contains(text.Replace("\"", "\\\"")))
            .Select(text => Shorten(text)).ToArray();
        Assert.True(gone.Length == 0, "Listed in extra-keys.json but not written by any lower layer:\n" + string.Join("\n", gone));
    }

    [Fact]
    public void ArgumentsAreFactsAndAreNeverLookedUp()
    {
        // "Cancel" is a button, but here it is a name the user gave to a recording: the sentence must not translate it.
        var shown = InLanguage("fr", () => Loc.T("Transcribing {0}", "Cancel"));
        Assert.EndsWith("Cancel", shown);
        // A caller that does mean a label translates it itself.
        Assert.EndsWith(Loc.Load("fr")["Cancel"], InLanguage("fr", () => Loc.T("Transcribing {0}", Loc.T("Cancel"))));
    }

    [Fact]
    public void AMessageFromALowerLayerAsAnArgumentGoesThroughDescribe()
    {
        // Lines that put an exception's message into a sentence: the message has to be said in the language first.
        var plain = new Regex(@"\bT\(\s*""(?:[^""\\]|\\.)*\{\d(?:[^""\\]|\\.)*""\s*,(?:(?!Describe\()[^;])*?\b(?:error|exception)\.Message\b");
        var problems = new List<string>();
        foreach (var path in TranslationSources.ShownTextFiles())
        {
            var number = 0;
            foreach (var line in File.ReadLines(path))
            {
                number++;
                if (plain.IsMatch(line.Split("//")[0])) problems.Add($"{Path.GetFileName(path)}:{number}: {Shorten(line.Trim(), 110)}");
            }
        }
        Assert.True(problems.Count == 0, "A message is put into a sentence without Loc.Describe(...):\n" + string.Join("\n", problems));
    }

    [Fact]
    public void NoTextShownByXamlIsABoundPropertyThatNobodyDecidedOn()
    {
        var problems = new List<string>();
        foreach (var path in TranslationSources.XamlFiles())
            foreach (var element in XDocument.Load(path).Descendants())
                foreach (var attribute in element.Attributes())
                {
                    var name = attribute.Name.LocalName;
                    var isText = name is "Text" or "Content" or "Header" or "ToolTip" or "AutomationProperties.Name" or "AutomationProperties.HelpText" || name == "Binding" && element.Name.LocalName == "DataGridTextColumn";
                    var bound = BoundPath().Match(attribute.Value);
                    if (!isText || !bound.Success) continue;
                    if (!TranslationSources.ShownAsTheyAre.Contains(bound.Groups[1].Value))
                        problems.Add($"{Path.GetFileName(path)}: {name}=\"{Shorten(attribute.Value)}\" (use {{local:Tr ...}}, or add it to ShownAsTheyAre if it is a fact)");
                }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [GeneratedRegex(@"^\{Binding\s+(?:Path=)?([\w.]+)")]
    private static partial Regex BoundPath();

    [Fact]
    public void ACountIsNeverWrittenInFrontOfAPluralNoun()
    {
        // "1 regions", "1 threads", "1 autres" read wrongly. A sentence names the count ("regions: 3") or has a variant of its own for 1.
        var plural = new Regex(@"\{\d+(?::[^}]*)?\}\s+(?:CPU\s+)?(?:regions|segments|threads|recordings|minutes|seconds|words|stages|files|models)\b");
        var allowed = new HashSet<string>
        {
            "Watching {0} · {1} recordings arriving",             // used from 2 on; "1 recording arriving" is its own text
            "{0} more recordings waiting",                        // used from 2 on; "1 more recording waiting" is its own text
            "The processing task timed out after {0} seconds.",   // a limit of minutes
            "{0}: choose 1–{1} CPU threads.",                     // a range
            "{0:0}% · {1}/{2} stages finished",                   // a fraction
            "{0:0.00} seconds per disagreement · model already loaded", // a measured time, not a count
        };
        var wrong = TranslationSources.UsedTexts().Where(text => plural.IsMatch(text) && !allowed.Contains(text)).Select(text => Shorten(text)).ToArray();
        Assert.True(wrong.Length == 0, "A count in front of a plural noun:\n" + string.Join("\n", wrong));
        foreach (var code in Translated)
        {
            var table = Loc.Load(code);
            Assert.NotEqual(table["{0} more recordings waiting"], table["1 more recording waiting"]);
        }
    }

    [Fact]
    public void SizesAreLabelledWithTheUnitTheyAreDividedBy()
    {
        // The app divides bytes by 1,048,576 (MiB). No text may call that "MB".
        var mb = new Regex(@"\{\d+(?::[^}]*)?\}\s*MB\b|\bMB\b\s*$");
        var wrong = TranslationSources.UsedTexts().Where(text => mb.IsMatch(text)).Select(text => Shorten(text)).ToArray();
        Assert.True(wrong.Length == 0, "A size labelled MB:\n" + string.Join("\n", wrong));
    }

    [Fact]
    public void TheBenchmarkRatioIsNamedInTheOrderItIsCalculated()
    {
        var row = new TriAsr.Benchmark.BenchmarkRow("Whisper", "model", "cpu", 4, "single", 10, [new(5), new(5), new(5)]);
        Assert.Equal(0.5, row.RealTimeFactor);    // processing time / audio time
        var texts = TranslationSources.UsedTexts();
        Assert.Contains("Time/audio ratio", texts);
        Assert.DoesNotContain(texts, text => text.Contains("Audio/time"));
    }

    [Fact]
    public async Task TheLanguageListsAreInAlphabeticalOrderOfTheNamesTheInterfaceShows()
    {
        var root = NewRoot(); var before = Loc.Instance.Language;
        try
        {
            using var host = App.App.CreateHost(root);
            var shell = host.Services.GetRequiredService<ShellViewModel>();
            await shell.InitializeAsync();
            foreach (var code in Translated)
            {
                shell.Language = code;
                var compare = CultureInfo.GetCultureInfo(code).CompareInfo;
                Assert.Equal("auto", shell.Languages[0].Code);                        // auto-detect stays first
                var names = shell.Languages.Skip(1).Select(LanguageText.Of).ToArray();
                for (var i = 1; i < names.Length; i++)
                    Assert.True(compare.Compare(names[i - 1], names[i], CompareOptions.IgnoreCase) <= 0, $"{code}: {names[i - 1]} before {names[i]}");
                var expansion = shell.ExpansionLanguages.Select(LanguageText.Of).ToArray();
                Assert.Equal(names, expansion);
            }
            shell.Language = "en";
            var store = host.Services.GetRequiredService<SettingsStore>();
            await WaitForAsync(async () => (await store.LoadAsync()).Language == "en");
            await WaitForAsync(() => Task.FromResult(shell.Status == "Preferences saved locally"));
        }
        finally { Loc.Instance.SetLanguage(before); TestCleanup.Delete(root); }
    }

    [Fact]
    public void TheShortPrivacyStatementAndThePolicyAgreeOnWhatGoesOnline()
    {
        // Models, runtimes and updates are downloaded, and updates are checked for: the short statement says so in every language.
        foreach (var code in Translated)
        {
            var statement = Loc.Load(code)["Transcription runs on this computer. Downloading models, runtimes or updates, and checking for updates, connects to GitHub or Hugging Face."];
            Assert.Contains("GitHub", statement);
            Assert.Contains("Hugging Face", statement);
            Assert.DoesNotContain("latest.json", statement); // the details belong to the policy
        }
        Assert.DoesNotContain("GitHub installation downloads also contact GitHub", Loc.PrivacyPolicy("en"));
    }
}
