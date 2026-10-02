using System.Text.Json;
using TriAsr.Domain;

namespace TriAsr.Application;

// File writes are supplied by the workspace boundary so the orchestrator owns no storage details.
public interface IJobWorkspace
{
    string DirectoryFor(Guid jobId);
    Task CreateAsync(TranscriptionJob job, CancellationToken cancellationToken);
}

public sealed class AudioJobQueue(IJobRepository repository, IJobWorkspace workspace, IAudioNormalizer audio)
{
    private readonly SemaphoreSlim _execution = new(1, 1);
    public event EventHandler<TranscriptionJob>? JobChanged;
    /// <param name="id">The id the job is to have, for a job whose id was given out before its file existed (a link that is still being fetched). Otherwise a new one.</param>
    public async Task<TranscriptionJob> EnqueueAsync(string source, string language, CancellationToken cancellationToken = default, Guid? id = null)
    {
        var job = new TranscriptionJob(id ?? Guid.NewGuid(), source, language, JobState.Queued, DateTimeOffset.UtcNow);
        await workspace.CreateAsync(job, cancellationToken);
        await repository.SaveAsync(job, cancellationToken);
        JobChanged?.Invoke(this, job);
        return job;
    }
    public async Task<TranscriptionJob> PrepareAsync(TranscriptionJob job, CancellationToken cancellationToken = default)
    {
        await _execution.WaitAsync(cancellationToken);
        try
        {
            job = job with { State = JobState.Preprocessing, Error = null };
            await UpdateAsync(job, cancellationToken);
            var directory = workspace.DirectoryFor(job.Id);
            await AudioPreparation.NormalizeAsync(audio, job.SourcePath, System.IO.Path.Combine(directory, "normalized.wav"), System.IO.Path.Combine(directory, "playback.m4a"), cancellationToken);
            job = job with { State = JobState.Queued, Checkpoint = "normalized" };
            await UpdateAsync(job, cancellationToken);
        }
        catch (OperationCanceledException) { job = job with { State = JobState.Cancelled }; await UpdateAsync(job, CancellationToken.None); }
        catch (Exception error) { job = job with { State = JobState.Failed, Error = error.Message }; await UpdateAsync(job, CancellationToken.None); }
        finally { _execution.Release(); }
        return job;
    }
    private async Task UpdateAsync(TranscriptionJob job, CancellationToken token)
    {
        await repository.SaveAsync(job, token);
        JobChanged?.Invoke(this, job);
    }
}
