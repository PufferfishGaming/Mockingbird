using TriAsr.Domain;

namespace TriAsr.Application;

/// <param name="Label">What the stage is doing, when that is not what its name says (telling the speakers apart, while comparing).</param>
public sealed record StageProgress(Guid JobId, JobState Stage, double Fraction, string? Label = null);
public interface IProgressReportingStages { event EventHandler<StageProgress>? StageProgressChanged; }
public sealed record TranscriptionProgress(Guid JobId, double Percent, string Stage, int CompletedStages, int TotalStages, bool IsRunning);

public sealed class TranscriptionProgressTracker(Guid jobId)
{
    private readonly object _sync = new();
    private readonly Dictionary<JobState, double> _fractions = new();
    private readonly HashSet<JobState> _completed = [];
    private string _stage = "Preparing transcription";
    private bool _running = true;
    /// <summary>What a recording on a server is doing while the sound of its link is downloaded, before any stage of the transcription.</summary>
    public const string LinkStage = "Downloading the link";
    /// <summary>What a recording is doing while its speakers are told apart (at the start of comparing the transcripts).</summary>
    public const string SpeakerStage = "Telling the speakers apart";
    public static string StageName(JobState stage) => stage switch
    {
        JobState.Preprocessing => "Preparing audio", JobState.DetectingLanguage => "Detecting language",
        JobState.RunningWhisper => "Whisper transcription", JobState.RunningCanary => "Canary transcription",
        JobState.Aligning => "Comparing transcripts", JobState.Correcting => "Reviewing disagreements",
        JobState.Finalizing => "Saving transcript", JobState.Complete => "Transcription complete",
        JobState.Cancelled => "Transcription cancelled", JobState.Failed => "Transcription failed", _ => "Preparing transcription"
    };
    public TranscriptionProgress Start(JobState stage, bool parallel = false)
    { lock (_sync) { _stage = parallel ? "Whisper and Canary transcription" : StageName(stage); return Snapshot(); } }
    public TranscriptionProgress Report(JobState stage, double fraction, string? label = null)
    {
        lock (_sync)
        {
            if (label is not null && stage is >= JobState.Preprocessing and <= JobState.Finalizing) _stage = label;
            if (double.IsFinite(fraction) && stage is >= JobState.Preprocessing and <= JobState.Finalizing)
                _fractions[stage] = Math.Max(_fractions.GetValueOrDefault(stage), Math.Clamp(fraction, 0, .99));
            return Snapshot();
        }
    }
    public TranscriptionProgress Complete(JobState stage)
    { lock (_sync) { _completed.Add(stage); _fractions[stage] = 1; return Snapshot(); } }
    public TranscriptionProgress Finish(JobState state)
    { lock (_sync) { _running = false; _stage = StageName(state); return Snapshot(); } }
    private TranscriptionProgress Snapshot() => new(jobId, _fractions.Values.Sum() / 7 * 100, _stage, _completed.Count, 7, _running);
}
