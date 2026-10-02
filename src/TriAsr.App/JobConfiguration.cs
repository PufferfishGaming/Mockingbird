using System.IO;

namespace TriAsr.App;

/// <summary>What a job decided when it started: the engines, their threads, and whether silence is skipped. Saved as configuration.json.</summary>
public sealed record JobConfiguration(string Fingerprint, string WhisperBackend, int WhisperThreads, string CanaryBackend,
    int CanaryThreads, string CorrectionBackend, int CorrectionThreads, bool ParallelSpeech, bool SkipNonSpeech = false)
{
    /// <summary>
    /// The configuration a job runs with. A new job takes the current Settings choice for skipping silence; a job that is resumed keeps the
    /// choice it started with, so changing Settings in between neither breaks it nor changes what the two engines were given.
    /// Everything else must still match what the job started with.
    /// </summary>
    public static JobConfiguration Bind(JobConfiguration current, JobConfiguration? saved, bool skipNonSpeechSetting)
    {
        if (saved is null) return current with { SkipNonSpeech = skipNonSpeechSetting };
        if (saved != current with { SkipNonSpeech = saved.SkipNonSpeech })
            throw new InvalidDataException("Runtime, model or hardware configuration changed. Create a new job before processing again.");
        return saved;
    }
}
