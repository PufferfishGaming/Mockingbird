using System.Text.Json.Serialization;

namespace TriAsr.Domain;

public enum JobState { Queued, Preprocessing, DetectingLanguage, RunningWhisper, RunningCanary, Aligning, Correcting, Finalizing, Complete, Failed, Cancelled }
public sealed record TranscriptionJob(Guid Id, string SourcePath, string Language, JobState State, DateTimeOffset CreatedUtc,
    string? Error = null, string? Checkpoint = null);
/// <param name="Speaker">Who says it ("1", "2"...), when the job tells the speakers apart; null otherwise.</param>
public sealed record TranscriptSegment(long StartMs, long EndMs, string Text, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Speaker = null);
public sealed record EngineTranscript(string Engine, string Model, string RuntimeVersion, string RequestedBackend,
    string ActualBackend, string Device, string Language, double AudioSeconds, double InferenceSeconds,
    IReadOnlyList<TranscriptSegment> Segments, string Text, bool NativeTimestamps, double? LoadSeconds = null, long? PeakRamBytes = null, double? CpuSeconds = null)
{
    public double RealTimeFactor => AudioSeconds > 0 ? InferenceSeconds / AudioSeconds : 0;
}
public sealed record ManualRevision(DateTimeOffset AtUtc, string PreviousText, string Text);
public sealed record FinalRegion(long StartMs, long EndMs, string FinalText, string WhisperText, string CanaryText,
    string Source, string? LlmChoice = null, double? Confidence = null, IReadOnlyList<ManualRevision>? Revisions = null, bool NativeTimestamps = true,
    IReadOnlyList<string>? Warnings = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Speaker = null);
public sealed record FinalTranscript(Guid JobId, string Language, IReadOnlyList<FinalRegion> Regions);

/// <summary>A stretch of a recording in which one speaker talks, as the speaker program found it. Speakers are numbered "1", "2"... in the order they first speak.</summary>
public sealed record SpeakerTurn(long StartMs, long EndMs, string Speaker);

/// <summary>A word of Whisper's output with the time it was said, for cutting a segment where the speaker changes.</summary>
/// <param name="Text">As Whisper wrote it, with the space in front of it.</param>
public sealed record TimedWord(long StartMs, long EndMs, string Text);

/// <summary>
/// What was chosen for a job beyond its language, kept in the job's folder (options.json).
/// </summary>
/// <param name="Speakers"><c>off</c>, <c>auto</c> (tell the speakers apart and find how many there are) or how many there are (<c>2</c> to <c>8</c>).</param>
public sealed record JobOptions(string Speakers = "off")
{
    public const int MostSpeakers = 8;

    /// <summary>Reads a choice of speakers as an upload or a window gives it; null when it is not one.</summary>
    public static string? NormalizeSpeakers(string? value)
    {
        var text = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(text) || text is "off" or "no" or "none" or "0") return "off";
        if (text is "auto" or "yes" or "on") return "auto";
        return int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) && count is >= 2 and <= MostSpeakers ? count.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    public bool TellsSpeakersApart => Speakers != "off";

    /// <summary>How many speakers the person said there are; 0 when the program is to find out.</summary>
    public int SpeakerCount => int.TryParse(Speakers, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) ? count : 0;
}
