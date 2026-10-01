using TriAsr.Application;
using TriAsr.Domain;

namespace TriAsr.Ui.SmokeTests;

public sealed class TranscriptionProgressTests
{
    [Fact]
    public void ParallelEnginesAndRetriedReportsNeverMoveProgressBackwards()
    {
        var tracker = new TranscriptionProgressTracker(Guid.NewGuid());
        tracker.Complete(JobState.Preprocessing); tracker.Complete(JobState.DetectingLanguage);
        tracker.Start(JobState.RunningWhisper, parallel: true);
        var first = tracker.Report(JobState.RunningWhisper, .8);
        var second = tracker.Report(JobState.RunningCanary, .5);
        var retry = tracker.Report(JobState.RunningWhisper, .1);
        Assert.True(second.Percent > first.Percent);
        Assert.Equal(second.Percent, retry.Percent);
        Assert.Equal("Whisper and Canary transcription", retry.Stage);
        Assert.Equal(2, retry.CompletedStages);
        Assert.True(tracker.Complete(JobState.RunningCanary).Percent > retry.Percent);
    }
    [Theory]
    [InlineData(JobState.Cancelled)]
    [InlineData(JobState.Failed)]
    public void StoppedWorkDoesNotPretendToReachOneHundredPercent(JobState state)
    {
        var tracker = new TranscriptionProgressTracker(Guid.NewGuid());
        tracker.Complete(JobState.Preprocessing);
        var stopped = tracker.Finish(state);
        Assert.False(stopped.IsRunning);
        Assert.InRange(stopped.Percent, 0, 99);
        Assert.Equal(1, stopped.CompletedStages);
    }
    [Fact]
    public void NativeCallbacksCannotCompleteStagesBeforeTheirCheckpointsAreSaved()
    {
        var tracker = new TranscriptionProgressTracker(Guid.NewGuid());
        Assert.Equal(0, tracker.Report(JobState.RunningWhisper, 1).CompletedStages);
        foreach (var stage in new[] { JobState.Preprocessing, JobState.DetectingLanguage, JobState.RunningWhisper,
            JobState.RunningCanary, JobState.Aligning, JobState.Correcting, JobState.Finalizing }) tracker.Complete(stage);
        var final = tracker.Finish(JobState.Complete);
        Assert.Equal(100, final.Percent);
        Assert.Equal(7, final.CompletedStages);
        Assert.False(final.IsRunning);
    }
}
