using System.Buffers.Binary;
using TriAsr.Application;
using TriAsr.Audio.Live;

namespace TriAsr.Audio.Tests;

/// <summary>Cutting the microphone's sound into phrases for live dictation, and tidying the text that comes back.</summary>
public sealed class LiveDictationTests
{
    private const int Rate = 16_000;

    /// <summary>A tone of the given loudness (RMS, 0 to 1) and length, as 16-bit PCM.</summary>
    private static byte[] Tone(double seconds, double rms, double hertz = 220)
    {
        var count = (int)(seconds * Rate);
        var bytes = new byte[count * 2];
        var peak = rms * Math.Sqrt(2) * 32767;
        for (var i = 0; i < count; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)(Math.Sin(2 * Math.PI * hertz * i / Rate) * peak));
        return bytes;
    }

    /// <summary>The faint hiss of a quiet room.</summary>
    private static byte[] Room(double seconds, double rms = 0.002, int seed = 1)
    {
        var random = new Random(seed);
        var count = (int)(seconds * Rate);
        var bytes = new byte[count * 2];
        var peak = rms * Math.Sqrt(3) * 32767;                 // uniform noise has an RMS of peak / sqrt(3)
        for (var i = 0; i < count; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)((random.NextDouble() * 2 - 1) * peak));
        return bytes;
    }

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    private static List<Utterance> Run(byte[] sound, int piece = 3200, UtteranceOptions? options = null, bool flush = true)
    {
        var found = new List<Utterance>();
        var detector = new UtteranceDetector(found.Add, options);
        for (var at = 0; at < sound.Length; at += piece) detector.Feed(sound.AsSpan(at, Math.Min(piece, sound.Length - at)));
        if (flush) detector.Flush();
        return found;
    }

    [Fact]
    public void ARoomWithNothingSaidGivesNoPhrase()
    {
        Assert.Empty(Run(Room(5)));
        Assert.Empty(Run(Tone(5, 0)));
        Assert.Empty(Run(Room(8, rms: 0.008)));                    // a louder room, still below what counts as speech
    }

    [Fact]
    public void APhraseWithPausesAroundItIsCutOutWithALittleBeforeAndAfter()
    {
        var found = Run(Join(Room(1), Tone(1.5, 0.08), Room(1.5)));
        var phrase = Assert.Single(found);
        Assert.InRange(phrase.Speech.TotalSeconds, 1.4, 1.6);
        Assert.InRange(phrase.Duration.TotalSeconds, 1.5 + 0.25, 1.5 + 0.30 + 0.3 + 0.1);        // the speech, its tail, and a pre-roll of up to 0.3 s
        Assert.Equal(phrase.Pcm.Length, (int)Math.Round(phrase.Duration.TotalSeconds * Rate) * 2);
        Assert.True(phrase.Duration > phrase.Speech);
    }

    [Fact]
    public void ALongPauseSeparatesTwoPhrasesAndAShortOneDoesNot()
    {
        Assert.Equal(2, Run(Join(Room(0.5), Tone(1, 0.08), Room(1.2), Tone(1, 0.08), Room(1))).Count);
        var together = Assert.Single(Run(Join(Room(0.5), Tone(1, 0.08), Room(0.4), Tone(1, 0.08), Room(1))));
        Assert.InRange(together.Speech.TotalSeconds, 1.9, 2.1);                                     // one phrase with a breath in the middle
    }

    [Fact]
    public void ASoundTooShortToBeSpeechIsThrownAway()
    {
        Assert.Empty(Run(Join(Room(1), Tone(0.1, 0.1), Room(1.5))));                              // a click
        Assert.Empty(Run(Join(Room(1), Tone(0.25, 0.1), Room(1.5))));                             // a cough
        Assert.Single(Run(Join(Room(1), Tone(0.45, 0.1), Room(1.5))));                            // "yes"
    }

    [Theory]
    [InlineData(3200)] [InlineData(777)] [InlineData(1)] [InlineData(640)] [InlineData(100_000)]
    public void ThePiecesTheSoundArrivesInDoNotMatter(int piece)
    {
        var sound = Join(Room(0.7), Tone(1.2, 0.07), Room(1.3), Tone(0.8, 0.05, 330), Room(1.1));
        var expected = Run(sound);
        var found = Run(sound, piece);
        Assert.Equal(expected.Count, found.Count);
        for (var i = 0; i < found.Count; i++) { Assert.Equal(expected[i].Duration, found[i].Duration); Assert.Equal(expected[i].Pcm, found[i].Pcm); }
    }

    [Fact]
    public void ANoisyRoomIsLearntSoThatItsHissIsNotSpeechButTalkOverItIs()
    {
        var found = Run(Join(Room(3, rms: 0.02, seed: 3), Tone(1.2, 0.12), Room(2, rms: 0.02, seed: 4)));        // a fan: 0.02 is above the fixed minimum of 0.012
        var phrase = Assert.Single(found);
        Assert.InRange(phrase.Speech.TotalSeconds, 1.0, 1.4);
        Assert.Empty(Run(Room(10, rms: 0.02, seed: 5)));
    }

    [Fact]
    public void ASoftFirstWordIsNotCutOffBecauseTheSoundBeforeItIsKept()
    {
        // 0.2 s of a very soft start (below the threshold), then the loud part
        var found = Run(Join(Room(1), Tone(0.2, 0.006), Tone(1, 0.09), Room(1.5)));
        var phrase = Assert.Single(found);
        Assert.True(phrase.Duration.TotalSeconds >= 1.0 + 0.2 + 0.2, phrase.Duration.ToString());
    }

    [Fact]
    public void AHugeStretchOfSpeechIsCutAtItsQuietestMomentAndNothingIsLost()
    {
        var nearlyQuiet = Tone(0.04, 0.0135);                                                      // a dip that is still counted as speech
        var long30 = Join(Room(0.5), Tone(14, 0.08), nearlyQuiet, Tone(16, 0.08), Room(1.5));
        var found = Run(long30);
        Assert.True(found.Count >= 2);
        Assert.All(found, phrase => Assert.True(phrase.Duration.TotalSeconds <= 26, phrase.Duration.ToString()));
        Assert.InRange(found.Sum(phrase => phrase.Speech.TotalSeconds), 29.0, 30.3);
    }

    [Fact]
    public void WhenDictationStopsAPhraseThatIsHalfSaidIsHandedOnAndTheDetectorStartsFresh()
    {
        var found = new List<Utterance>();
        var detector = new UtteranceDetector(found.Add);
        detector.Feed(Join(Room(0.5), Tone(1.2, 0.08)));
        Assert.True(detector.InSpeech);
        Assert.Empty(found);
        detector.Flush();
        Assert.Single(found);
        Assert.False(detector.InSpeech);
        detector.Flush();                                                                           // nothing pending: nothing more
        Assert.Single(found);
        detector.Feed(Join(Room(0.6), Tone(1, 0.08), Room(1.2)));                                   // and it can be used again
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void TheLevelFollowsTheLoudnessOfTheLatestSound()
    {
        var detector = new UtteranceDetector(_ => { });
        detector.Feed(Room(0.5));
        Assert.InRange(detector.Level, 0, 0.01);
        detector.Feed(Tone(0.2, 0.1));
        Assert.InRange(detector.Level, 0.08, 0.12);
        Assert.True(detector.Threshold >= 0.012);
    }

    [Fact]
    public void TheDetectorTakesWhatTheOptionsSay()
    {
        var sound = Join(Room(0.5), Tone(1, 0.08), Room(0.9), Tone(1, 0.08), Room(1.5));
        Assert.Equal(2, Run(sound).Count);
        Assert.Single(Run(sound, options: new UtteranceOptions(EndSilenceMs: 1500)));              // it waits longer before it ends a phrase
        Assert.Empty(Run(sound, options: new UtteranceOptions(MinSpeechMs: 2500)));
    }

    // ---- the text ----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(" Hello there.\n", "Hello there.")]
    [InlineData(" [BLANK_AUDIO]\n", "")]
    [InlineData(" [Music] And then we left. [Applause]", "And then we left.")]
    [InlineData("(music) Welcome back (applause)", "Welcome back")]
    [InlineData("  Two   lines\nin one ", "Two lines in one")]
    [InlineData("♪ la la la ♪ Real words", "Real words")]
    [InlineData("He said (quietly) that it works", "He said (quietly) that it works")]       // brackets that are speech stay
    [InlineData("", "")]
    [InlineData(null, "")]
    public void TheTextOfAPhraseIsTidiedForTyping(string? raw, string expected) => Assert.Equal(expected, PhraseText.Clean(raw));

    [Theory]
    [InlineData("you", 0.5, true)] [InlineData("Thank you.", 0.8, true)] [InlineData("Thanks for watching!", 1.0, true)] [InlineData("  ", 0.5, true)] [InlineData("...", 0.5, true)]
    [InlineData("Thank you.", 2.5, false)]                                                    // said at length it is a real sentence
    [InlineData("Thank you for the invitation.", 0.8, false)]
    [InlineData("Meet me at nine", 0.6, false)]
    public void WordsWhisperInventsForSoundWithoutSpeechAreRecognisedOnlyInShortPhrases(string text, double speechSeconds, bool phantom) =>
        Assert.Equal(phantom, PhraseText.IsPhantom(text, TimeSpan.FromSeconds(speechSeconds)));

    [Fact]
    public void APhraseIsWrappedAsAWavFileThatSpeechProgramsRead()
    {
        var pcm = Tone(0.5, 0.05);
        var wav = PhraseText.Wav(pcm);
        Assert.Equal(44 + pcm.Length, wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVEfmt ", System.Text.Encoding.ASCII.GetString(wav, 8, 8));
        Assert.Equal(16_000u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)));
        Assert.Equal((ushort)16, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal((uint)pcm.Length, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40)));
        Assert.Equal(pcm, wav.AsSpan(44).ToArray());
    }
}
