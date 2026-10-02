namespace TriAsr.Domain;

/// <summary>A stretch of detected speech in the canonical 16 kHz recording, in milliseconds.</summary>
public sealed record SpeechSpan(long StartMs, long EndMs);

/// <summary>One piece of the shared timeline. Speech chunks go to the engines; non-speech chunks only keep the timeline complete.</summary>
public sealed record AudioChunk(int Index, long StartMs, long EndMs, bool IsSpeech, bool OverlapsPrevious)
{
    public long DurationMs => EndMs - StartMs;
}

/// <summary>The chunk plan for one job: computed once, saved with the job, and shared by every engine.</summary>
public sealed record ChunkPlan(int Version, long DurationMs, string Source, IReadOnlyList<AudioChunk> Chunks)
{
    public const int CurrentVersion = 1;
    public IEnumerable<AudioChunk> SpeechChunks() => Chunks.Where(chunk => chunk.IsSpeech);
    public long SpeechMs() => SpeechChunks().Sum(chunk => chunk.DurationMs);
}
