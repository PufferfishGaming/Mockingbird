using TriAsr.Domain;

namespace TriAsr.Application;

/// <summary>Limits for the shared chunk plan. Times are milliseconds.</summary>
/// <param name="TargetMs">Preferred longest stretch of speech in one chunk.</param>
/// <param name="HardCapMs">No chunk is ever longer than this, padding included.</param>
/// <param name="CutSilenceMs">A silence at least this long is a good place to cut.</param>
/// <param name="WeakCutSilenceMs">A shorter pause is used only when no good silence fits.</param>
/// <param name="OverlapMs">Overlap given to the second chunk when speech has to be cut in the middle.</param>
/// <param name="PadMs">Silence kept around speech so a chunk does not start or end on a word.</param>
/// <param name="SeparateSilenceMs">A silence at least this long becomes its own non-speech chunk.</param>
public sealed record ChunkOptions(long TargetMs = 30_000, long HardCapMs = 35_000, long CutSilenceMs = 300,
    long WeakCutSilenceMs = 100, long OverlapMs = 1_000, long PadMs = 200, long SeparateSilenceMs = 2_000)
{
    /// <summary>Longest speech core: padding or a merged short silence can add up to this much on each side.</summary>
    public long CoreCapMs => HardCapMs - 2 * SeparateSilenceMs;
    public void Validate()
    {
        if (TargetMs <= 0 || OverlapMs < 0 || PadMs < 0 || WeakCutSilenceMs < 0 || SeparateSilenceMs <= 0)
            throw new ArgumentException("Chunk options must be positive.");
        if (WeakCutSilenceMs > CutSilenceMs) throw new ArgumentException("The weak cut silence cannot be longer than the cut silence.");
        if (SeparateSilenceMs < CutSilenceMs) throw new ArgumentException("The separating silence cannot be shorter than the cut silence.");
        if (OverlapMs >= TargetMs) throw new ArgumentException("The overlap must be shorter than the target length.");
        if (PadMs > SeparateSilenceMs / 2) throw new ArgumentException("Padding cannot exceed half of the separating silence.");
        if (TargetMs > CoreCapMs) throw new ArgumentException("The target length plus padding must fit inside the hard cap.");
    }
}

/// <summary>
/// Turns VAD speech spans into one chunk plan that tiles the whole recording. Cuts go into silences of at least
/// <see cref="ChunkOptions.CutSilenceMs"/> where possible; speech is cut in the middle only when it runs longer
/// than the cap without any pause, and then the second chunk overlaps the first.
/// </summary>
public static class ChunkPlanner
{
    private sealed record Core(long Start, long End, bool Forced);

    public static ChunkPlan Plan(long durationMs, IEnumerable<SpeechSpan> speech, ChunkOptions? options = null, string source = "vad")
    {
        var o = options ?? new ChunkOptions();
        o.Validate();
        if (durationMs < 0) throw new ArgumentOutOfRangeException(nameof(durationMs));
        if (durationMs == 0) return new(ChunkPlan.CurrentVersion, 0, source, []);
        var spans = Normalize(speech, durationMs);
        if (spans.Count == 0) return new(ChunkPlan.CurrentVersion, durationMs, source, [new AudioChunk(0, 0, durationMs, false, false)]);
        return new(ChunkPlan.CurrentVersion, durationMs, source, Tile(durationMs, Group(spans, o), o));
    }

    private static List<SpeechSpan> Normalize(IEnumerable<SpeechSpan> speech, long durationMs)
    {
        var merged = new List<SpeechSpan>();
        foreach (var span in speech.Select(item => new SpeechSpan(Math.Max(0, item.StartMs), Math.Min(durationMs, item.EndMs)))
                     .Where(item => item.EndMs > item.StartMs).OrderBy(item => item.StartMs).ThenBy(item => item.EndMs))
        {
            if (merged.Count > 0 && span.StartMs <= merged[^1].EndMs) merged[^1] = merged[^1] with { EndMs = Math.Max(merged[^1].EndMs, span.EndMs) };
            else merged.Add(span);
        }
        return merged;
    }

    /// <summary>Packs spans into speech cores of at most the target length, preferring the longest silences that fit.</summary>
    private static List<Core> Group(List<SpeechSpan> spans, ChunkOptions o)
    {
        var cores = new List<Core>();
        var index = 0; long? overrideStart = null; var forced = false;
        while (index < spans.Count)
        {
            var start = overrideStart ?? spans[index].StartMs;
            int strongInTarget = -1, strongInCap = -1, weakInTarget = -1, weakInCap = -1;
            for (var k = index; k < spans.Count; k++)
            {
                var length = spans[k].EndMs - start;
                if (length > o.CoreCapMs) break;
                var gap = k == spans.Count - 1 ? long.MaxValue : spans[k + 1].StartMs - spans[k].EndMs;
                if (gap < o.WeakCutSilenceMs) continue;
                var inTarget = length <= o.TargetMs;
                if (gap >= o.CutSilenceMs) { if (inTarget) strongInTarget = k; else if (strongInCap < 0) strongInCap = k; }
                else if (inTarget) weakInTarget = k;
                else if (weakInCap < 0) weakInCap = k;
                // A long silence is never packed into a chunk: engines tend to invent text for it.
                if (gap >= o.SeparateSilenceMs) break;
            }
            var best = strongInTarget >= 0 ? strongInTarget : strongInCap >= 0 ? strongInCap : weakInTarget >= 0 ? weakInTarget : weakInCap;
            if (best >= 0)
            {
                cores.Add(new(start, spans[best].EndMs, forced));
                index = best + 1; overrideStart = null; forced = false;
                continue;
            }
            // Continuous speech beyond the cap: cut at the target length and let the next chunk start earlier.
            var cut = start + o.TargetMs;
            cores.Add(new(start, cut, forced));
            var next = cut - o.OverlapMs;
            while (index < spans.Count && spans[index].EndMs <= next) index++;
            overrideStart = next; forced = true;
        }
        return cores;
    }

    /// <summary>Adds the padding and the non-speech chunks so that the chunks cover the whole recording.</summary>
    private static List<AudioChunk> Tile(long durationMs, List<Core> cores, ChunkOptions o)
    {
        var chunks = new List<AudioChunk>();
        void Add(long start, long end, bool speech, bool overlap) { if (end > start) chunks.Add(new(chunks.Count, start, end, speech, overlap)); }
        long start0 = 0;
        var lead = cores[0].Start;
        if (lead >= o.SeparateSilenceMs) { start0 = lead - o.PadMs; Add(0, start0, false, false); }
        var begin = start0;
        for (var i = 0; i < cores.Count; i++)
        {
            long end; var nextBegin = 0L; var gapChunk = false;
            if (i == cores.Count - 1)
            {
                var tail = durationMs - cores[i].End;
                end = tail >= o.SeparateSilenceMs ? cores[i].End + o.PadMs : durationMs;
                gapChunk = end < durationMs;
            }
            else if (cores[i + 1].Forced) { end = cores[i].End; nextBegin = cores[i + 1].Start; }
            else
            {
                var gap = cores[i + 1].Start - cores[i].End;
                if (gap >= o.SeparateSilenceMs) { end = cores[i].End + o.PadMs; nextBegin = cores[i + 1].Start - o.PadMs; gapChunk = true; }
                else { end = cores[i].End + gap / 2; nextBegin = end; }
            }
            Add(begin, end, true, cores[i].Forced);
            if (gapChunk) Add(end, i == cores.Count - 1 ? durationMs : nextBegin, false, false);
            begin = nextBegin;
        }
        return chunks;
    }
}
