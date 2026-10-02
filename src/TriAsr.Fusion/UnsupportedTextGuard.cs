using TriAsr.Domain;

namespace TriAsr.Fusion;

/// <summary>A Whisper segment that was left out, with how much of it the other engine's text contained.</summary>
/// <param name="Support">The share of its words that occur anywhere in the other engine's text.</param>
/// <param name="PairSupport">The share of its pairs of neighbouring words that occur next to each other in the other engine's text.</param>
public sealed record UnsupportedSegment(int Index, long StartMs, long EndMs, string Text, double Support, double PairSupport);

/// <summary>
/// Over game sound, silence or the very end of a recording Whisper writes text that is not there ("subtitles by ...", invented
/// sentences). Such text is left out only when two independent signals agree: the speech detector found no speech in the stretch,
/// and the other engine (Canary) did not write these words. Either signal alone is not enough: singing counts as non-speech for the
/// detector, and Canary may simply miss a word. A sung line is kept because Canary heard it too.
/// <para>
/// A segment is left out when it has at least <see cref="MinimumWords"/> words, lies wholly inside one non-speech chunk of the
/// chunk plan (give or take <see cref="ToleranceMs"/>), and either fewer than <see cref="MinimumSupport"/> of its words occur anywhere
/// in the other engine's text or fewer than <see cref="MinimumPairSupport"/> of its word pairs occur next to each other there. The pair
/// test matters for long texts: single common words occur somewhere in a long text by chance, a sequence of two rarely does.
/// </para>
/// <para>
/// Measured (the other engine's text has no timestamps, so the test is global): on a song nothing is left out and the error rate stays
/// 5.2% - its weakest genuine line still has half of its pairs in Canary's text; on a German clip the invented last line goes
/// (agreement with Canary 9.0% to 2.1%); on a game video 49 of 110 segments go and the text is about as close to the narration as before
/// but with nearly all of it present.
/// </para>
/// </summary>
public static class UnsupportedTextGuard
{
    public const double MinimumSupport = 0.5;
    public const double MinimumPairSupport = 0.3;
    public const int MinimumWords = 3;
    public const long ToleranceMs = 200;

    /// <param name="otherEngineText">The other engine's whole text; with no text there is no second opinion and nothing is removed.</param>
    /// <param name="chunks">The chunk plan; a chunk with <see cref="AudioChunk.IsSpeech"/> false is a stretch without detected speech.</param>
    public static (IReadOnlyList<TranscriptSegment> Kept, IReadOnlyList<UnsupportedSegment> Removed) Remove(
        IReadOnlyList<TranscriptSegment> segments, string otherEngineText, IReadOnlyList<AudioChunk> chunks)
    {
        var other = TranscriptQuality.Normalize(otherEngineText ?? "");
        if (other.Length == 0 || chunks.Count == 0 || segments.Count == 0) return (segments, []);
        var otherWords = other.Split(' ');
        var knownWords = new HashSet<string>(otherWords);
        var knownPairs = new HashSet<string>();
        for (var i = 0; i + 1 < otherWords.Length; i++) knownPairs.Add(otherWords[i] + " " + otherWords[i + 1]);
        var silent = chunks.Where(chunk => !chunk.IsSpeech).ToArray();
        var removed = new List<UnsupportedSegment>(); var kept = new List<TranscriptSegment>();
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var normalized = TranscriptQuality.Normalize(segment.Text);
            var words = normalized.Length == 0 ? [] : normalized.Split(' ');
            var inSilence = silent.Any(chunk => segment.StartMs >= chunk.StartMs - ToleranceMs && segment.EndMs <= chunk.EndMs + ToleranceMs);
            if (words.Length >= MinimumWords && inSilence)
            {
                var support = words.Count(knownWords.Contains) / (double)words.Length;
                var pairs = Enumerable.Range(0, words.Length - 1).Count(i => knownPairs.Contains(words[i] + " " + words[i + 1])) / (double)(words.Length - 1);
                if (support < MinimumSupport || pairs < MinimumPairSupport)
                {
                    removed.Add(new(index, segment.StartMs, segment.EndMs, segment.Text.Trim(), Math.Round(support, 2), Math.Round(pairs, 2)));
                    continue;
                }
            }
            kept.Add(segment);
        }
        return removed.Count == 0 ? (segments, removed) : (kept, removed);
    }
}
