using Microsoft.Extensions.DependencyInjection;
using TriAsr.Domain;
using TriAsr.Persistence;

namespace TriAsr.Persistence.Tests;

public sealed class JobRepositoryTests
{
    [Fact]
    public async Task JobsRetainCheckpointAfterRepositoryRecreation()
    {
        var root = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new TriAsr.Infrastructure.StoragePaths(root); paths.EnsureDirectories();
            var connections = new SqliteConnectionFactory(paths);
            var repository = new JobRepository(connections); await repository.InitializeAsync();
            var job = new TranscriptionJob(Guid.NewGuid(), "sample.m4a", "de", JobState.Preprocessing, DateTimeOffset.UtcNow);
            await repository.SaveAsync(job); await repository.SaveAsync(job with { State = JobState.Queued, Checkpoint = "normalized" });
            var restored = Assert.Single(await new JobRepository(connections).ListAsync());
            Assert.Equal(job.Id, restored.Id); Assert.Equal("normalized", restored.Checkpoint);
            await using var connection = await connections.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM job_events;";
            Assert.Equal(2L, await command.ExecuteScalarAsync());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
