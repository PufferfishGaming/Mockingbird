using Microsoft.Data.Sqlite;
using TriAsr.Application;
using TriAsr.Domain;
using TriAsr.Infrastructure;
using TriAsr.Persistence;

namespace TriAsr.Persistence.Tests;

/// <summary>Deleting a project against a real database and real folders: what goes, what stays.</summary>
public sealed class ProjectRemovalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly SqliteConnectionFactory _connections;
    private readonly JobRepository _repository;
    private readonly RecordRepository _records;
    private readonly JobWorkspace _workspace;
    private readonly ProjectRemoval _removal;

    public ProjectRemovalTests()
    {
        _paths = new StoragePaths(_root);
        _paths.EnsureDirectories();
        _connections = new SqliteConnectionFactory(_paths);
        _repository = new JobRepository(_connections);
        _repository.InitializeAsync().GetAwaiter().GetResult();
        _records = new RecordRepository(_connections);
        _workspace = new JobWorkspace(_paths);
        _removal = new ProjectRemoval(_repository, _workspace, _paths);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Own(params string[] parts) => Path.Combine([_root, .. parts]);

    private static string Write(string path, string content = "data")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>A project as the program makes it: a record, its history, a working folder with files, and records stored for it.</summary>
    private async Task<TranscriptionJob> ProjectAsync(string source, JobState state = JobState.Complete)
    {
        var job = new TranscriptionJob(Guid.NewGuid(), source, "de", JobState.Queued, DateTimeOffset.UtcNow);
        await _workspace.CreateAsync(job, default);                  // the working folder, with source.json
        await _repository.SaveAsync(job);
        job = job with { State = state };
        await _repository.SaveAsync(job);                            // a second event in its history
        var folder = _workspace.DirectoryFor(job.Id);
        Write(Path.Combine(folder, "normalized.wav")); Write(Path.Combine(folder, "playback.m4a")); Write(Path.Combine(folder, "final.json"), "{}");
        Write(Path.Combine(folder, "Revisions", "1.json"), "{}");
        await _records.SaveAsync(new("final", job.Id.ToString("N") + "/final.json", "{}", job.Id));
        await _records.SaveAsync(new("manual_revisions", job.Id.ToString("N") + "/Revisions/1.json", "{}", job.Id));
        return job;
    }

    private async Task<long> CountAsync(string table, string where = "1=1")
    {
        await using var connection = await _connections.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {where};";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task ADeletedProjectLeavesNoRecordHistoryStoredTranscriptOrWorkingFileBehindAndNothingOfOtherProjects()
    {
        var chosen = Write(Own("Music", "interview.wav"));
        var gone = await ProjectAsync(chosen);
        var kept = await ProjectAsync(Write(Own("Music", "other.wav")));
        await _records.SaveAsync(new("settings", "appearance", "{}"));                    // belongs to no project

        var leftovers = await _removal.DeleteAsync(gone);

        Assert.Empty(leftovers);
        Assert.Equal(kept.Id, Assert.Single(await _repository.ListAsync()).Id);
        Assert.Equal(0, await CountAsync("job_events", $"job_id='{gone.Id}'"));
        Assert.Equal(2, await CountAsync("job_events", $"job_id='{kept.Id}'"));
        Assert.Equal(0, await CountAsync("workspace_records", $"job_id='{gone.Id}'"));
        Assert.Equal(2, await CountAsync("workspace_records", $"job_id='{kept.Id}'"));
        Assert.Equal(1, await CountAsync("workspace_records", "kind='settings'"));
        Assert.False(Directory.Exists(_workspace.DirectoryFor(gone.Id)));
        Assert.True(Directory.Exists(_workspace.DirectoryFor(kept.Id)));
        Assert.True(File.Exists(chosen));                                                   // the recording the person chose is theirs
    }

    [Fact]
    public async Task TheCopyOfARecordingTheProgramMadeItselfGoesWithTheProjectButOnlyThatFolder()
    {
        var upload = Write(Own("Api", "Incoming", "aaaa", "meeting.mp3"));
        var otherUpload = Write(Own("Api", "Incoming", "bbbb", "other.mp3"));
        var fetched = Write(Own("Links", "cccc", "Talk [id].webm"));
        var otherFetched = Write(Own("Links", "dddd", "Other.webm"));
        var uploaded = await ProjectAsync(upload);
        var fromLink = await ProjectAsync(fetched, JobState.Failed);

        await _removal.DeleteAsync(uploaded);
        await _removal.DeleteAsync(fromLink);

        Assert.False(Directory.Exists(Own("Api", "Incoming", "aaaa")));
        Assert.False(Directory.Exists(Own("Links", "cccc")));
        Assert.True(File.Exists(otherUpload) && File.Exists(otherFetched));                // another project's copy
        Assert.True(Directory.Exists(Own("Api", "Incoming")) && Directory.Exists(Own("Links")));
    }

    [Fact]
    public async Task AFileThatLooksLikeTheProgramsFoldersButIsNotInThemIsNeverTouched()
    {
        var lookalikes = new[]
        {
            Write(Own("Links", "loose.wav")),                                              // directly in the folder: no folder of its own
            Write(Own("Links-backup", "x", "talk.wav")),                                    // a name that merely starts the same
            Write(Own("Api", "Incoming", "direct.wav")),
            Write(Own("Elsewhere", "Links", "y", "talk.wav")),                              // a folder of the same name somewhere else
            Write(Own("Recordings", "voice.wav"))                                          // the person's own recordings
        };
        foreach (var path in lookalikes)
        {
            var project = await ProjectAsync(path);
            Assert.Equal([_workspace.DirectoryFor(project.Id)], _removal.FoldersOf(project));
            await _removal.DeleteAsync(project);
            Assert.True(File.Exists(path), path);
        }
    }

    [Fact]
    public async Task APathThatClimbsOutOfTheProgramsFoldersIsIgnored()
    {
        var outside = Write(Own("Outside", "secret.wav"));
        var climbing = Own("Links", "eeee", "..", "..", "Outside", "secret.wav");
        var project = await ProjectAsync(climbing);
        Assert.Equal([_workspace.DirectoryFor(project.Id)], _removal.FoldersOf(project));
        await _removal.DeleteAsync(project);
        Assert.True(File.Exists(outside));
    }

    [Theory]
    [InlineData(JobState.Complete, true)] [InlineData(JobState.Failed, true)] [InlineData(JobState.Cancelled, true)]
    [InlineData(JobState.Queued, false)] [InlineData(JobState.Preprocessing, false)] [InlineData(JobState.RunningWhisper, false)] [InlineData(JobState.Finalizing, false)]
    public async Task OnlyAProjectThatIsNotBeingWorkedOnCanBeDeleted(JobState state, bool allowed)
    {
        Assert.Equal(allowed, ProjectRemoval.CanDelete(state));
        var project = await ProjectAsync(Write(Own("Music", "a.wav")), state);
        if (allowed) { await _removal.DeleteAsync(project); Assert.Empty(await _repository.ListAsync()); }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => _removal.DeleteAsync(project));
            Assert.Single(await _repository.ListAsync());                                    // nothing was touched
            Assert.True(Directory.Exists(_workspace.DirectoryFor(project.Id)));
        }
    }

    [Fact]
    public async Task AFileThatIsStillInUseIsReportedWhileTheProjectIsGoneFromTheList()
    {
        var project = await ProjectAsync(Write(Own("Music", "a.wav")));
        var folder = _workspace.DirectoryFor(project.Id);
        List<string> leftovers;
        using (new FileStream(Path.Combine(folder, "playback.m4a"), FileMode.Open, FileAccess.Read, FileShare.None))   // a player that has not let go
            leftovers = [.. await _removal.DeleteAsync(project)];
        Assert.Equal([folder], leftovers);
        Assert.Empty(await _repository.ListAsync());                                           // the person no longer sees it
        Assert.Equal(0, await CountAsync("workspace_records", $"job_id='{project.Id}'"));
    }

    [Fact]
    public async Task APlayerThatLetsGoJustAfterTheDeletionStartsDoesNotLeaveFilesBehind()
    {
        var project = await ProjectAsync(Write(Own("Music", "a.wav")));
        var folder = _workspace.DirectoryFor(project.Id);
        var held = new FileStream(Path.Combine(folder, "playback.m4a"), FileMode.Open, FileAccess.Read, FileShare.None);
        _ = Task.Run(async () => { await Task.Delay(350); held.Dispose(); });
        Assert.Empty(await _removal.DeleteAsync(project));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task DeletingSomethingThatIsNotThereIsHarmless()
    {
        await _repository.DeleteAsync(Guid.NewGuid());
        var project = await ProjectAsync(Write(Own("Music", "a.wav")));
        await _removal.DeleteAsync(project);
        await _removal.DeleteAsync(project);                                                    // twice
        Assert.Empty(await _repository.ListAsync());
    }
}
