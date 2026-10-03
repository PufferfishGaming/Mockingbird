using TriAsr.Application;
using TriAsr.Domain;

namespace TriAsr.Audio.Tests;

/// <summary>Telling the speakers of a recording apart (<see cref="SpeakerTurns"/>): the choice a job keeps, tidying the program's turns, and cutting segments where the speaker changes.</summary>
public sealed class SpeakerTests
{
    [Theory]
    [InlineData(null, "off")] [InlineData("", "off")] [InlineData("off", "off")] [InlineData("0", "off")] [InlineData(" AUTO ", "auto")] [InlineData("2", "2")] [InlineData("8", "8")]
    [InlineData("1", null)] [InlineData("9", null)] [InlineData("-3", null)] [InlineData("lots", null)] [InlineData("2.5", null)]
    public void AChoiceOfSpeakersIsReadOneWay(string? value, string? read) => Assert.Equal(read, JobOptions.NormalizeSpeakers(value));

    [Fact]
    public void AJobKnowsWhetherItsSpeakersAreToldApartAndHowManyThereAre()
    {
        Assert.False(new JobOptions().TellsSpeakersApart);
        Assert.Equal((true, 0), (new JobOptions("auto").TellsSpeakersApart, new JobOptions("auto").SpeakerCount));
        Assert.Equal((true, 3), (new JobOptions("3").TellsSpeakersApart, new JobOptions("3").SpeakerCount));
    }

    // ---- the names people give the speakers ----------------------------------------------------------------------------------------------

    private static FinalRegion Said(string text, string? speaker) => new(0, 1000, text, text, "", "agreement", Speaker: speaker);

    [Fact]
    public void ATranscriptListsItsSpeakersInTheOrderOfTheirNumbersAndNamesThemOnlyWithAName()
    {
        var transcript = new FinalTranscript(Guid.NewGuid(), "en", [Said("a", "2"), Said("b", "10"), Said("c", null), Said("d", "1"), Said("e", "2")],
            new Dictionary<string, string> { ["2"] = "Bea", ["10"] = "" });
        Assert.Equal(["1", "2", "10"], transcript.Speakers);
        Assert.Equal("Bea", transcript.NameOf("2"));
        Assert.Null(transcript.NameOf("10"));                                                      // an empty name is no name
        Assert.Null(transcript.NameOf("1"));
        Assert.Null(transcript.NameOf(null));
        Assert.Empty(new FinalTranscript(Guid.NewGuid(), "en", [Said("a", null)]).Speakers);
    }

    [Fact]
    public void OnlyTheNamesWorthKeepingAreKept()
    {
        string[] speakers = ["1", "2", "3"];
        var names = SpeakerNames.Clean(new Dictionary<string, string>
        {
            ["1"] = "  Anna\tKovács \n\u0007", ["2"] = "   ", ["3"] = new string('x', 80), ["7"] = "Nobody"
        }, speakers)!;
        Assert.Equal("Anna Kovács", names["1"]);                                                   // on one line, single spaces, no control characters
        Assert.False(names.ContainsKey("2"));                                                      // empty
        Assert.Equal(SpeakerNames.MaxLength, names["3"].Length);
        Assert.False(names.ContainsKey("7"));                                                      // the transcript has no such speaker
        Assert.Null(SpeakerNames.Clean(new Dictionary<string, string> { ["1"] = " " }, speakers)); // nothing left: no names at all
        Assert.Null(SpeakerNames.Clean(null, speakers));
    }

    [Fact]
    public void TheNamesAreKeptWithTheTranscriptAndATranscriptWithoutThemIsWrittenAsBefore()
    {
        var named = new FinalTranscript(Guid.NewGuid(), "en", [Said("a", "1")], new Dictionary<string, string> { ["1"] = "Anna" });
        var json = System.Text.Json.JsonSerializer.Serialize(named);
        Assert.Equal("Anna", System.Text.Json.JsonSerializer.Deserialize<FinalTranscript>(json)!.NameOf("1"));
        Assert.DoesNotContain("SpeakerNames", System.Text.Json.JsonSerializer.Serialize(named with { SpeakerNames = null }));
        Assert.DoesNotContain("Speakers", System.Text.Json.JsonSerializer.Serialize(named with { SpeakerNames = null }));   // the list of speakers is worked out, not stored
    }

    private static SpeakerTurn Turn(double start, double end, string speaker) => new((long)(start * 1000), (long)(end * 1000), speaker);

    [Fact]
    public void ASpeakerWithAlmostNoSpeechIsMergedIntoTheSpeakersAroundIt()
    {
        // The clustering found a third "speaker" for one short stretch (2 % of the speech): it is a mistake, not a person.
        SpeakerTurn[] found = [Turn(0, 20, "speaker_03"), Turn(20, 21, "speaker_07"), Turn(21.5, 40, "speaker_01"), Turn(40, 50, "speaker_03")];
        var turns = SpeakerTurns.Tidy(found);
        Assert.Equal([Turn(0, 21, "1"), Turn(21.5, 40, "2"), Turn(40, 50, "1")], turns);           // numbered in the order they first speak, neighbours of one speaker joined
    }

    [Fact]
    public void GivenHowManyThereAreTheOnesWhoTalkMostAreKept()
    {
        SpeakerTurn[] found = [Turn(0, 10, "a"), Turn(10, 14, "b"), Turn(14, 30, "c"), Turn(30, 33, "d")];
        Assert.Equal(["1", "2"], SpeakerTurns.Tidy(found, count: 2).Select(turn => turn.Speaker).Distinct());
        Assert.Equal(4, SpeakerTurns.Tidy(found, count: 6).Select(turn => turn.Speaker).Distinct().Count());   // more than were found: all are kept
        Assert.Equal(4, SpeakerTurns.Tidy(found).Select(turn => turn.Speaker).Distinct().Count());              // each has well over 4 % of the speech
        Assert.Empty(SpeakerTurns.Tidy([]));
    }

    [Fact]
    public void TheSpeakerAtAMomentIsTheOneTalkingOrTheNearest()
    {
        SpeakerTurn[] turns = [Turn(0, 10, "1"), Turn(8, 12, "2"), Turn(20, 30, "1")];
        Assert.Equal("1", SpeakerTurns.At(turns, 5_000));
        Assert.Equal("2", SpeakerTurns.At(turns, 9_000));                                             // two at once: the one who began last
        Assert.Equal("2", SpeakerTurns.At(turns, 14_000));                                            // nobody: the nearest turn
        Assert.Equal("1", SpeakerTurns.MostOf(turns, 0, 12_000));
        Assert.Null(SpeakerTurns.At([], 0));
    }

    private static TimedWord Word(double start, double end, string text) => new((long)(start * 1000), (long)(end * 1000), text);

    [Fact]
    public void ASegmentIsCutWhereTheSpeakerChangesInsideIt()
    {
        // Whisper wrote one segment over a question and its answer; the speaker program heard two people.
        var segment = new TranscriptSegment(1_000, 5_000, "Are you coming? Yes, at nine.");
        TimedWord[] words = [Word(1.0, 1.3, " Are"), Word(1.3, 1.5, " you"), Word(1.5, 2.0, " coming?"), Word(3.0, 3.4, " Yes,"), Word(3.4, 3.6, " at"), Word(3.6, 4.0, " nine.")];
        SpeakerTurn[] turns = [Turn(0.8, 2.2, "1"), Turn(2.8, 4.5, "2")];
        Assert.Equal([new TranscriptSegment(1_000, 3_000, "Are you coming?", "1"), new TranscriptSegment(3_000, 5_000, "Yes, at nine.", "2")], SpeakerTurns.Split([segment], words, turns));
    }

    [Fact]
    public void ASegmentWhoseWordsDoNotMakeItsTextGoesWholeToWhoTalksMost()
    {
        var segment = new TranscriptSegment(1_000, 5_000, "Something cleaned up later.");
        TimedWord[] words = [Word(1.0, 2.0, " Something"), Word(3.0, 4.0, " else")];
        SpeakerTurn[] turns = [Turn(0, 1.5, "1"), Turn(1.5, 5, "2")];
        Assert.Equal([segment with { Speaker = "2" }], SpeakerTurns.Split([segment], words, turns));
        Assert.Equal([segment with { Speaker = "2" }], SpeakerTurns.Split([segment], [], turns));      // no words at all
        Assert.Equal([segment], SpeakerTurns.Split([segment], words, []));                              // no speakers: nothing changes
    }

    [Fact]
    public void SegmentsOfOneSpeakerStayWhole()
    {
        TranscriptSegment[] segments = [new(0, 2_000, "Hello there."), new(2_000, 4_000, "How are you?")];
        TimedWord[] words = [Word(0, 0.5, " Hello"), Word(0.5, 1.5, " there."), Word(2.1, 2.5, " How"), Word(2.5, 2.8, " are"), Word(2.8, 3.5, " you?")];
        Assert.Equal(segments.Select(segment => segment with { Speaker = "1" }), SpeakerTurns.Split(segments, words, [Turn(0, 4, "1")]));
    }
}
