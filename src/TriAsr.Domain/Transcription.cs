namespace TriAsr.Domain;

public enum JobState { Queued, Preprocessing, DetectingLanguage, RunningWhisper, RunningCanary, Aligning, Correcting, Finalizing, Complete, Failed, Cancelled }
public sealed record TranscriptionJob(Guid Id, string SourcePath, string Language, JobState State, DateTimeOffset CreatedUtc,
    string? Error = null, string? Checkpoint = null);
public sealed record TranscriptSegment(long StartMs, long EndMs, string Text);
public sealed record EngineTranscript(string Engine, string Model, string RuntimeVersion, string RequestedBackend,
    string ActualBackend, string Device, string Language, double AudioSeconds, double InferenceSeconds,
    IReadOnlyList<TranscriptSegment> Segments, string Text, bool NativeTimestamps, double? LoadSeconds = null, long? PeakRamBytes = null)
{
    public double RealTimeFactor => AudioSeconds > 0 ? InferenceSeconds / AudioSeconds : 0;
}
public sealed record ManualRevision(DateTimeOffset AtUtc, string PreviousText, string Text);
public sealed record FinalRegion(long StartMs, long EndMs, string FinalText, string WhisperText, string CanaryText,
    string Source, string? LlmChoice = null, double? Confidence = null, IReadOnlyList<ManualRevision>? Revisions = null, bool NativeTimestamps = true,
    IReadOnlyList<string>? Warnings = null);
public sealed record FinalTranscript(Guid JobId, string Language, IReadOnlyList<FinalRegion> Regions);
