using TriAsr.Application;
using TriAsr.Domain;

namespace TriAsr.Audio.Tests;

/// <summary>
/// Which language a phrase of live dictation is written in when the person speaks two (<see cref="LiveLanguagePicker"/>). The readings below are the ones measured on a
/// Hungarian speaker reading English and Hungarian (detection and confidence as whisper-cli gave them).
/// </summary>
public sealed class LiveLanguageTests
{
    /// <summary>Stands in for the speech program: one reading per language, and a list of the languages it was asked for.</summary>
    private sealed class Program(params SpeechReading[] readings)
    {
        public List<string> Asked { get; } = [];

        public Task<SpeechReading> ReadAsync(string language, CancellationToken token)
        {
            Asked.Add(language);
            return Task.FromResult(language == "auto" ? readings[0] : readings.Skip(1).First(reading => reading.Language == language));
        }
    }

    private static Task<LivePhrase> Pick(Program program, string language, string? recent = null) => LiveLanguagePicker.PickAsync(program.ReadAsync, language, recent, default);

    private static SpeechReading Auto(string language, double detection, double confidence, string text) => new(language, detection, confidence, text);
    private static SpeechReading Told(string language, double confidence, string text) => new(language, 1, confidence, text);

    [Fact]
    public async Task EnglishThatTheProgramHeardAsHungarianIsWrittenInEnglish()
    {
        var program = new Program(Auto("hu", 0.923, -0.794, "A szulfur az a 10. és a legnagyobb element a világban."), Told("en", -0.068, "Sulfur is the tenth most abundant element by mass."));
        Assert.Equal(new LivePhrase("Sulfur is the tenth most abundant element by mass.", "en"), await Pick(program, "en+hu"));
        Assert.Equal(["auto", "en"], program.Asked);                                            // the detected language is not read a second time
    }

    [Fact]
    public async Task HungarianThatTheProgramIsSureOfIsTakenAfterOneReading()
    {
        var program = new Program(Auto("hu", 0.998, -0.091, "Nevéhez fűződik a magyar Hold-radar-kísérlet."), Told("en", -0.624, "The name is inspired by the Hungarian radar experiment."));
        Assert.Equal(new LivePhrase("Nevéhez fűződik a magyar Hold-radar-kísérlet.", "hu"), await Pick(program, "en+hu"));
        Assert.Equal(["auto"], program.Asked);
    }

    [Fact]
    public async Task ACloseCallGoesToTheLanguageOfThePreviousPhraseAndAClearWinSwitches()
    {
        // Measured: after two English phrases, this one read slightly better as a Hungarian translation (-0.104) than in the English it was spoken in (-0.165).
        SpeechReading[] readings = [Auto("hu", 0.841, -0.104, "Ez azt jelenti, hogy a kétből kis szolgálat."), Told("en", -0.165, "Which means burning stone.")];
        Assert.Equal("en", (await Pick(new Program(readings), "en+hu", recent: "en")).Language);
        Assert.Equal("hu", (await Pick(new Program(readings), "en+hu", recent: null)).Language);
        // A Hungarian phrase after English ones: Hungarian reads far better (-0.09 against -0.68), so the language switches at once.
        Assert.Equal("hu", (await Pick(new Program(Auto("hu", 0.97, -0.091, "Bay Zoltán magyar fizikus."), Told("en", -0.680, "Zoltán Bay, a Hungarian physicist.")), "en+hu", recent: "en")).Language);
    }

    [Fact]
    public async Task ALanguageOutsideThePairIsNeverTheAnswerBothOfThePairAreRead()
    {
        var program = new Program(Auto("zh", 0.95, -0.3, "你好"), Told("de", -0.08, "Ich spiele sehr gerne Basketball."), Told("en", -0.37, "I really like to play basketball."));
        Assert.Equal(new LivePhrase("Ich spiele sehr gerne Basketball.", "de"), await Pick(program, "de+en"));
        Assert.Equal(["auto", "de", "en"], program.Asked);
    }

    [Fact]
    public async Task TheCreditsWhisperInventsForSilenceAndReadingsWithoutWordsAreNeverChosen()
    {
        var credit = new Program(Auto("en", 0.646, -0.614, "Thank you."), Told("hu", -0.213, "Feliratok az Amara.org közösségétől"));
        Assert.Equal(new LivePhrase("Thank you.", "en"), await Pick(credit, "en+hu"));
        var nothing = new Program(Auto("en", 0.38, -1.169, "*whistling*"), Told("hu", -0.152, "*szállás*"));
        Assert.Equal(new LivePhrase("", ""), await Pick(nothing, "en+hu"));
    }

    [Fact]
    public async Task OneLanguageOrAutoDetectIsOneReadingAsBefore()
    {
        var program = new Program(Auto("hu", 0.84, -0.8, " Valami [BLANK_AUDIO] "), Told("en", -0.07, "Something."));
        Assert.Equal(new LivePhrase("Valami", "hu"), await Pick(program, "auto"));
        Assert.Equal(new LivePhrase("Something.", "en"), await Pick(program, "en", recent: "hu"));
        Assert.Equal(new LivePhrase("Valami", "hu"), await Pick(program, "klingon+en"));         // a choice that cannot be read is auto-detect
        Assert.Equal(["auto", "en", "auto"], program.Asked);
    }

    [Theory]
    [InlineData(null, "auto")] [InlineData("", "auto")] [InlineData("auto", "auto")] [InlineData("en", "en")] [InlineData("en+hu", "en+hu")] [InlineData(" HU + en ", "hu+en")]
    [InlineData("en hu", "en+hu")] [InlineData("en,hu", "en+hu")] [InlineData("en+en", "en")]
    public void AChoiceOfLanguagesIsReadAndWrittenOneWay(string? value, string written)
    {
        Assert.True(LanguageCatalog.TryParseChoice(value, out var codes));
        Assert.Equal(written, LanguageCatalog.JoinChoice(codes));
    }

    [Theory]
    [InlineData("klingon")] [InlineData("en+klingon")] [InlineData("auto+en")] [InlineData("en+hu+de")] [InlineData("+")]
    public void AChoiceThatNamesAnUnknownLanguageOrTooManyIsRefused(string value) => Assert.False(LanguageCatalog.TryParseChoice(value, out _));

    [Theory]
    [InlineData("Feliratok az Amara.org közösségétől", 3.0, true)]                            // said for a long stretch of silence it is still invented
    [InlineData("Untertitelung des ZDF, 2020", 0.3, true)]
    [InlineData("Subtitles by the Amara.org community", 2.0, true)]
    [InlineData("I read the Amara.org page about how volunteers subtitle videos and it was a long and interesting article", 6.0, false)]
    [InlineData("Die Untertitel sind zu klein.", 2.0, false)]                                  // the word for subtitles is not a credit
    public void TheCreditsOfSubtitledVideosAreNotSomethingSomeoneSaid(string text, double speechSeconds, bool phantom) =>
        Assert.Equal(phantom, PhraseText.IsPhantom(text, TimeSpan.FromSeconds(speechSeconds)));

    [Theory]
    [InlineData("*whistling*", "")] [InlineData("* Vogelgezwitscher * Hallo", "Hallo")] [InlineData("2 * 3 * 4", "2 * 3 * 4")]
    public void SoundsWhisperWritesBetweenAsterisksAreNotTyped(string raw, string expected) => Assert.Equal(expected, PhraseText.Clean(raw));
}
