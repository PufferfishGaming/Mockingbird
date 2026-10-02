using TriAsr.Domain;

namespace TriAsr.Application;

/// <summary>One stretch of the recording that Canary reads as a unit. Index is the chunk's number in the saved plan.</summary>
public sealed record CanaryWindow(int Index, long StartMs, long EndMs, bool IsSpeech, bool OverlapsPrevious)
{
    public long DurationMs => EndMs - StartMs;

    /// <summary>
    /// The speech windows of a saved chunk plan, used when the job skips silence and music; null when the plan does not describe this
    /// recording (the caller then reads the whole file). Without skipping Canary always reads the whole file in its own windows: measured
    /// on a song, Canary read from isolated non-speech pieces returned a fifth fewer words than the same audio in whole windows.
    /// </summary>
    public static IReadOnlyList<CanaryWindow>? SpeechWindows(ChunkPlan plan, long audioMs)
    {
        if (plan.Version != ChunkPlan.CurrentVersion || Math.Abs(plan.DurationMs - audioMs) > 2) return null;
        return plan.Chunks.Where(chunk => chunk.IsSpeech)
            .Select(chunk => new CanaryWindow(chunk.Index, chunk.StartMs, chunk.EndMs, chunk.IsSpeech, chunk.OverlapsPrevious)).ToList();
    }
}

/// <param name="Windows">When given, Canary reads exactly these windows (in order); otherwise it reads the whole file in its own windows.</param>
/// <param name="CheckpointDirectory">Where finished windows are saved, so a restarted run continues instead of starting over.</param>
public sealed record CanaryRequest(string RuntimeDirectory, string Model, string Audio, string Language, string Backend, int Threads, string Output,
    IReadOnlyList<CanaryWindow>? Windows = null, string? CheckpointDirectory = null);
