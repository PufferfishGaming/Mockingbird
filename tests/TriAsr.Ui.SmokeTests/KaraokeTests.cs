using System.IO;
using System.Text.RegularExpressions;
using TriAsr.App;
using TriAsr.Domain;

namespace TriAsr.Ui.SmokeTests;

/// <summary>Karaoke-style playback (ADR-0017): the time of a region shared out over its words, and the region the recording is in.</summary>
public sealed class KaraokeTests
{
    [Fact]
    public void TheTimeOfARegionIsSharedOutOverItsWordsByLengthAndTheyCoverItWithoutGaps()
    {
        const string text = "Please wait a moment.";
        var words = KaraokePlan.Words(text);
        Assert.Equal(["Please", "wait", "a", "moment."], words.Select(word => text.Substring(word.Start, word.Length)));
        Assert.Equal(0, words[0].From);
        Assert.Equal(1, words[^1].To);
        for (var i = 1; i < words.Count; i++) Assert.Equal(words[i - 1].To, words[i].From, 12);   // each word starts where the one before ended
        // weights: 6, 4, 1, 6 letters + 4 for the full stop = 10 -> 21 in all
        Assert.Equal(6 / 21.0, words[0].To, 9);
        Assert.Equal(10 / 21.0, words[1].To, 9);
        Assert.Equal(11 / 21.0, words[2].To, 9);
        Assert.True(words[3].To - words[3].From > words[0].To - words[0].From);                    // the pause after the full stop gives the last word more time
    }

    [Fact]
    public void AClauseGetsALittleMoreTimeAndASentenceMore()
    {
        var plain = KaraokePlan.Words("one two");
        var comma = KaraokePlan.Words("one, two");
        var stop = KaraokePlan.Words("one. two");
        Assert.True(comma[0].To - comma[0].From > plain[0].To - plain[0].From);
        Assert.True(stop[0].To - stop[0].From > comma[0].To - comma[0].From);
    }

    [Fact]
    public void WordsAreSplitOnAnyWhitespaceAndAccentedLettersCount()
    {
        var text = "  Szép\tnapot  kívánok\r\nneked ";
        var words = KaraokePlan.Words(text);
        Assert.Equal(["Szép", "napot", "kívánok", "neked"], words.Select(word => text.Substring(word.Start, word.Length)));
        Assert.Equal(2, KaraokePlan.Words("Szép napot").Count);
        Assert.Empty(KaraokePlan.Words(""));
        Assert.Empty(KaraokePlan.Words("   \t "));
        Assert.Equal(-1, KaraokePlan.WordAt(KaraokePlan.Words(""), 0.5));
        var single = KaraokePlan.Words("...");                      // no letters at all: still one word with a time
        Assert.Single(single);
        Assert.Equal((0.0, 1.0), (single[0].From, single[0].To));
    }

    [Theory]
    [InlineData(0.0, 0)] [InlineData(0.2, 0)] [InlineData(0.29, 1)] [InlineData(0.5, 2)] [InlineData(0.99, 3)] [InlineData(1.0, 3)] [InlineData(2.0, 3)]
    public void TheWordBeingSaidIsTheOneWhoseTimeItIs(double progress, int expected)
    {
        var words = KaraokePlan.Words("Please wait a moment.");
        Assert.Equal(expected, KaraokePlan.WordAt(words, progress));
    }

    private static ReviewRegion Region(long start, long end, bool native = true) =>
        new(new FinalRegion(start, end, "text", "text", "text", "agreement", NativeTimestamps: native));

    [Fact]
    public void TheRegionTheRecordingIsInIsFoundAndSaysHowFarItHasCome()
    {
        var regions = new[] { Region(1000, 3000), Region(3000, 7000), Region(9000, 10000) };
        Assert.Null(PlaybackFollower.Follow(regions, 500));                     // before the first
        Assert.All(regions, region => Assert.Equal(-1, region.PlayProgress));

        Assert.Same(regions[0], PlaybackFollower.Follow(regions, 1000));        // a region starts at its start...
        Assert.Equal(0, regions[0].PlayProgress);
        Assert.Same(regions[0], PlaybackFollower.Follow(regions, 2000));
        Assert.Equal(0.5, regions[0].PlayProgress, 9);
        Assert.Same(regions[1], PlaybackFollower.Follow(regions, 3000));        // ...and ends where the next one starts
        Assert.Equal(-1, regions[0].PlayProgress);
        Assert.Equal(0.25, PlaybackFollower.Follow(regions, 4000) is { } now ? now.PlayProgress : -9, 9);

        Assert.Null(PlaybackFollower.Follow(regions, 8000));                    // a gap belongs to nobody
        Assert.All(regions, region => Assert.Equal(-1, region.PlayProgress));
        Assert.Null(PlaybackFollower.Follow(regions, 10000));                   // after the last
        Assert.Same(regions[2], PlaybackFollower.Follow(regions, 9500));
    }

    [Fact]
    public void RegionsWithoutTheirOwnTimeAreNeverMarkedAndClearingRemovesTheMarks()
    {
        var untimed = Region(0, 5000, native: false);
        var timed = Region(0, 5000);
        Assert.Same(timed, PlaybackFollower.Follow([untimed, timed], 1000));
        Assert.Equal(-1, untimed.PlayProgress);
        PlaybackFollower.Clear([untimed, timed]);
        Assert.Equal(-1, timed.PlayProgress);
        Assert.Null(PlaybackFollower.Follow([], 1000));
    }

    [Fact]
    public void ARegionIsToldOfAChangeOnlyWhenItsProgressChanges()
    {
        var region = Region(0, 1000);
        var changes = 0;
        region.PropertyChanged += (_, change) => { if (change.PropertyName == nameof(ReviewRegion.PlayProgress)) changes++; };
        PlaybackFollower.Follow([region], 500);
        PlaybackFollower.Follow([region], 500);
        PlaybackFollower.Follow([region], 500);
        Assert.Equal(1, changes);
        PlaybackFollower.Follow([region], 600);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void TheWebPageSharesOutTheTimeTheSameWayAsTheProgram()
    {
        var script = File.ReadAllText(Path.Combine(TranslationSources.AppFolder, "Web", "app.js"));
        Assert.Contains($"SENTENCE_PAUSE = {KaraokePlan.SentencePause}, CLAUSE_PAUSE = {KaraokePlan.ClausePause}", script);
        Assert.Contains("\".!?…\".includes(last) ? SENTENCE_PAUSE", script);
        Assert.Contains("\",;:–—\".includes(last) ? CLAUSE_PAUSE", script);
        Assert.Contains("Math.max(1, letters) + pause", script);
        Assert.Contains("found[found.length - 1].to = 1", script);
        Assert.Contains("\\S+", script);                                   // split on whitespace
        Assert.Contains("[\\p{L}\\p{N}]", script);                         // letters and digits count
        // and the program's own copy of the same rules
        var plan = File.ReadAllText(Path.Combine(TranslationSources.AppFolder, "KaraokePlan.cs"));
        Assert.Contains("'.' or '!' or '?' or '…' => SentencePause", plan);
        Assert.Contains("',' or ';' or ':' or '–' or '—' => ClausePause", plan);
    }
}
