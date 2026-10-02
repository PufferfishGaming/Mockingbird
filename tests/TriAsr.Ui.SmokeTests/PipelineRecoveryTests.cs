using TriAsr.Application;
using TriAsr.Domain;
using TriAsr.App;

namespace TriAsr.Ui.SmokeTests;

public sealed class PipelineRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanaryFailureCanFinishReviewWhenRecoveryIsSupported(bool parallel)
    {
        var stages = new FailingStages(parallel, JobState.RunningCanary);
        var pipeline = new TranscriptionPipeline(new MemoryRepository(), stages);
        var result = await pipeline.RunAsync(NewJob());
        Assert.Equal(JobState.Complete, result.State);
        Assert.Contains(JobState.RunningWhisper, stages.Executed);
        Assert.Contains(JobState.Finalizing, stages.Executed);
        Assert.Equal(1, stages.Recoveries);
        Assert.True(new ReviewRegion(new(0, 1000, "Text", "Text", "", "single-asr-needs-listening")).IsUncertain);
    }
    [Fact]
    public async Task UnrecoverableWhisperFailureDoesNotPretendToComplete()
    {
        var stages = new FailingStages(false, JobState.RunningWhisper);
        var result = await new TranscriptionPipeline(new MemoryRepository(), stages).RunAsync(NewJob());
        Assert.Equal(JobState.Failed, result.State);
        Assert.DoesNotContain(JobState.Finalizing, stages.Executed);
    }
    [Fact]
    public async Task CancellationNeverTurnsIntoASpeechFallback()
    {
        var stages = new FailingStages(false, JobState.RunningCanary, cancel: true);
        var result = await new TranscriptionPipeline(new MemoryRepository(), stages).RunAsync(NewJob());
        Assert.Equal(JobState.Cancelled, result.State);
        Assert.Equal(0, stages.Recoveries);
    }
    private static TranscriptionJob NewJob() => new(Guid.NewGuid(), "sample.wav", "de", JobState.Queued, DateTimeOffset.UtcNow);
    private sealed class MemoryRepository : IJobRepository
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAsync(TranscriptionJob job, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<TranscriptionJob>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TranscriptionJob>>([]);
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class FailingStages(bool parallel, JobState failure, bool cancel = false) : ITranscriptionStages
    {
        public List<JobState> Executed { get; } = [];
        public int Recoveries { get; private set; }
        public Task ExecuteAsync(TranscriptionJob job, JobState stage, CancellationToken token)
        {
            Executed.Add(stage);
            if (stage == failure) throw cancel ? new OperationCanceledException() : new InvalidOperationException("Runtime failure");
            return Task.CompletedTask;
        }
        public Task<bool> CanRunSpeechParallelAsync(TranscriptionJob job, CancellationToken token) => Task.FromResult(parallel);
        public Task<bool> RecoverSpeechFailureAsync(TranscriptionJob job, JobState stage, Exception error, CancellationToken token)
        { Recoveries++; return Task.FromResult(stage == JobState.RunningCanary); }
        public Task<FinalTranscript> LoadFinalAsync(Guid id, CancellationToken token = default) => throw new NotSupportedException();
        public Task SaveManualAsync(FinalTranscript transcript, CancellationToken token = default) => throw new NotSupportedException();
    }
}
