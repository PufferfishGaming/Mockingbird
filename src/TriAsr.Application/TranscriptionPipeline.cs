using TriAsr.Domain;

namespace TriAsr.Application;

public interface ITranscriptionStages
{
    Task ExecuteAsync(TranscriptionJob job, JobState stage, CancellationToken token);
    Task<FinalTranscript> LoadFinalAsync(Guid id, CancellationToken token = default);
    Task SaveManualAsync(FinalTranscript transcript, CancellationToken token = default);
    Task<bool> CanRunSpeechParallelAsync(TranscriptionJob job, CancellationToken token) => Task.FromResult(false);
    Task<bool> RecoverSpeechFailureAsync(TranscriptionJob job, JobState stage, Exception error, CancellationToken token) => Task.FromResult(false);
}

public sealed class TranscriptionPipeline(IJobRepository repository, ITranscriptionStages stages)
{
    private readonly SemaphoreSlim _execution = new(1, 1);
    public event EventHandler<TranscriptionJob>? JobChanged;
    public event EventHandler<TranscriptionProgress>? ProgressChanged;
    private static readonly JobState[] Steps = [JobState.Preprocessing, JobState.DetectingLanguage,
        JobState.RunningWhisper, JobState.RunningCanary, JobState.Aligning, JobState.Correcting, JobState.Finalizing];
    public async Task<TranscriptionJob> RunAsync(TranscriptionJob job, CancellationToken token = default)
    {
        await _execution.WaitAsync(token);
        var progress = new TranscriptionProgressTracker(job.Id);
        void OnStageProgress(object? sender, StageProgress update)
        { if (update.JobId == job.Id) ProgressChanged?.Invoke(this, progress.Report(update.Stage, update.Fraction)); }
        var reporting = stages as IProgressReportingStages;
        if (reporting is not null) reporting.StageProgressChanged += OnStageProgress;
        try
        {
            var speechParallel = false;
            foreach (var stage in Steps)
            {
                if (stage == JobState.RunningCanary && speechParallel) continue;
                job = job with { State = stage, Error = null };
                await UpdateAsync(job, token);
                var parallel = stage == JobState.RunningWhisper && await stages.CanRunSpeechParallelAsync(job, token);
                ProgressChanged?.Invoke(this, progress.Start(stage, parallel));
                async Task ExecuteTrackedAsync(JobState current)
                {
                    await ExecuteStageAsync(job, current, token);
                    ProgressChanged?.Invoke(this, progress.Complete(current));
                }
                if (parallel)
                {
                    speechParallel = true;
                    await Task.WhenAll(ExecuteTrackedAsync(JobState.RunningWhisper), ExecuteTrackedAsync(JobState.RunningCanary));
                }
                else await ExecuteTrackedAsync(stage);
                job = job with { Checkpoint = stage.ToString() };
                await UpdateAsync(job, token);
            }
            job = job with { State = JobState.Complete };
            await UpdateAsync(job, token);
        }
        catch (OperationCanceledException) { job = job with { State = JobState.Cancelled }; await UpdateAsync(job, CancellationToken.None); }
        catch (Exception error) { job = job with { State = JobState.Failed, Error = error.Message }; await UpdateAsync(job, CancellationToken.None); }
        finally
        {
            if (reporting is not null) reporting.StageProgressChanged -= OnStageProgress;
            try { ProgressChanged?.Invoke(this, progress.Finish(job.State)); }
            finally { _execution.Release(); }
        }
        return job;
    }
    private async Task ExecuteStageAsync(TranscriptionJob job, JobState stage, CancellationToken token)
    {
        try { await stages.ExecuteAsync(job, stage, token); }
        catch (Exception error) when (error is not OperationCanceledException && stage is JobState.RunningWhisper or JobState.RunningCanary)
        {
            if (!await stages.RecoverSpeechFailureAsync(job, stage, error, token)) throw;
        }
    }
    private async Task UpdateAsync(TranscriptionJob job, CancellationToken token)
    {
        await repository.SaveAsync(job, token);
        JobChanged?.Invoke(this, job);
    }
}
