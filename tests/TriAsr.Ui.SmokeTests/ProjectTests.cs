using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TriAsr.App;
using TriAsr.Application;
using TriAsr.Domain;

namespace TriAsr.Ui.SmokeTests;

/// <summary>The history on Studio's Projects page: a click opens a finished project in Review, and a project can be deleted.</summary>
public sealed class ProjectTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));

    private sealed record Seeded(Guid Id, string Source, string Folder);

    private static async Task<Seeded> SeedAsync(IHost host, string root, string name, JobState state, bool withTranscript = false, string? source = null, int minutesAgo = 0)
    {
        var repository = host.Services.GetRequiredService<IJobRepository>();
        var workspace = host.Services.GetRequiredService<IJobWorkspace>();
        var path = source ?? Path.Combine(root, "Music", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) await File.WriteAllBytesAsync(path, new byte[2000]);
        var id = Guid.NewGuid();
        var folder = workspace.DirectoryFor(id);
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "normalized.wav"), new byte[1000]);
        if (withTranscript)
        {
            var transcript = new FinalTranscript(id, "de",
            [
                new FinalRegion(0, 2500, "Guten Tag.", "Guten Tag.", "Guten Tag.", "agreement", NativeTimestamps: true),
                new FinalRegion(2500, 6000, "Willkommen zur Sitzung.", "Willkommen zur Sitzung.", "Willkommen zur Sitzung.", "agreement", NativeTimestamps: true)
            ]);
            await File.WriteAllTextAsync(Path.Combine(folder, "final.json"), JsonSerializer.Serialize(transcript));
        }
        await repository.SaveAsync(new TranscriptionJob(id, path, "de", state, DateTimeOffset.UtcNow.AddMinutes(-minutesAgo)));
        return new Seeded(id, path, folder);
    }

    private static async Task<(IHost Host, ShellViewModel Shell, IJobRepository Repository)> StartAsync(string root, Func<IHost, Task>? seed = null)
    {
        var host = App.App.CreateHost(root);
        var repository = host.Services.GetRequiredService<IJobRepository>();
        await repository.InitializeAsync();
        if (seed is not null) await seed(host);
        var shell = host.Services.GetRequiredService<ShellViewModel>();
        await shell.InitializeAsync();
        return (host, shell, repository);
    }

    [Fact]
    public Task TheHistoryListsEveryProjectNewestFirstAndAClickOpensAFinishedOneInReview() => UiThread.RunAsync(async () =>
    {
        var root = NewRoot();
        Seeded finished = null!, failed = null!, older = null!;
        var (host, shell, _) = await StartAsync(root, async h =>
        {
            older = await SeedAsync(h, root, "old.wav", JobState.Complete, withTranscript: true, minutesAgo: 60);
            finished = await SeedAsync(h, root, "interview.wav", JobState.Complete, withTranscript: true, minutesAgo: 5);
            failed = await SeedAsync(h, root, "broken.wav", JobState.Failed, minutesAgo: 30);
        });
        using var _ = host;
        try
        {
            Assert.Equal([finished.Id, failed.Id, older.Id], shell.Jobs.Select(job => job.Id));          // newest first
            shell.SelectedPage = shell.Navigation.First(page => page.Name == "Projects");
            Assert.True(shell.IsJobsPage);

            await shell.OpenProjectCommand.ExecuteAsync(shell.Jobs[0]);                                  // a click on a finished project
            Assert.True(shell.IsReviewPage);
            Assert.Equal(finished.Id, shell.SelectedJob!.Id);
            Assert.Equal(["Guten Tag.", "Willkommen zur Sitzung."], shell.Regions.Select(region => region.Text));

            shell.SelectedPage = shell.Navigation.First(page => page.Name == "Projects");
            await shell.OpenProjectCommand.ExecuteAsync(shell.Jobs[1]);                                  // a click on one that failed
            Assert.True(shell.IsJobsPage);                                                               // nothing to open: it is only selected
            Assert.Equal(failed.Id, shell.SelectedJob!.Id);
            Assert.Equal(2, shell.Regions.Count);                                                        // and the review that was open stays as it was

            await shell.OpenProjectCommand.ExecuteAsync(null);                                           // a click on nothing
            await shell.OpenProjectCommand.ExecuteAsync(shell.Jobs[2]);                                  // another finished project replaces the review
            Assert.True(shell.IsReviewPage);
            Assert.Equal(older.Id, shell.SelectedJob!.Id);
            await shell.Host.DisposeAsync();
        }
        finally { TestCleanup.Delete(root); }
    });

    [Fact]
    public Task DeletingAProjectRemovesItsRecordsAndFilesAndTheProgramsCopyButNotTheRecordingThePersonChose() => UiThread.RunAsync(async () =>
    {
        var root = NewRoot();
        Seeded chosen = null!, fetched = null!, uploaded = null!, other = null!;
        var (host, shell, repository) = await StartAsync(root, async h =>
        {
            chosen = await SeedAsync(h, root, "interview.wav", JobState.Complete, withTranscript: true, minutesAgo: 1);
            fetched = await SeedAsync(h, root, "x", JobState.Complete, withTranscript: true, source: Path.Combine(root, "Links", "ab12cd34", "Talk [id].webm"), minutesAgo: 2);
            uploaded = await SeedAsync(h, root, "x", JobState.Failed, source: Path.Combine(root, "Api", "Incoming", Guid.NewGuid().ToString("N"), "meeting.mp3"), minutesAgo: 3);
            other = await SeedAsync(h, root, "keep.wav", JobState.Complete, withTranscript: true, minutesAgo: 4);
        });
        using var _ = host;
        try
        {
            await shell.OpenProjectCommand.ExecuteAsync(shell.Jobs.First(job => job.Id == chosen.Id));
            Assert.Equal(2, shell.Regions.Count);
            Assert.True(File.Exists(Path.Combine(chosen.Folder, "normalized.wav")));

            // the project under review: the review is closed, its working files and records go, the recording stays
            await shell.DeleteProjectCommand.ExecuteAsync(shell.Jobs.First(job => job.Id == chosen.Id));
            Assert.DoesNotContain(shell.Jobs, job => job.Id == chosen.Id);
            Assert.Empty(shell.Regions);
            Assert.Null(shell.SelectedJob);
            Assert.False(Directory.Exists(chosen.Folder));
            Assert.True(File.Exists(chosen.Source));
            Assert.Equal("Project deleted: interview.wav", shell.Status);
            var records = host.Services.GetRequiredService<IRecordRepository>();
            Assert.Empty(await records.ListAsync("final", chosen.Id));
            Assert.DoesNotContain(await repository.ListAsync(), job => job.Id == chosen.Id);

            // the sound fetched from a link and the copy a client uploaded were the program's own
            await shell.DeleteProjectCommand.ExecuteAsync(shell.Jobs.First(job => job.Id == fetched.Id));
            await shell.DeleteProjectCommand.ExecuteAsync(shell.Jobs.First(job => job.Id == uploaded.Id));
            Assert.False(Directory.Exists(Path.Combine(root, "Links", "ab12cd34")));
            Assert.False(Directory.Exists(Path.GetDirectoryName(uploaded.Source)!));
            Assert.False(Directory.Exists(fetched.Folder) || Directory.Exists(uploaded.Folder));

            // everything else is as it was, also after a restart of the list
            Assert.Equal([other.Id], shell.Jobs.Select(job => job.Id));
            Assert.Equal([other.Id], (await repository.ListAsync()).Select(job => job.Id));
            Assert.True(Directory.Exists(other.Folder) && File.Exists(other.Source));
            await shell.Host.DisposeAsync();
        }
        finally { TestCleanup.Delete(root); }
    });

    [Fact]
    public Task AProjectThatIsStillWaitingOrRunningCannotBeDeleted() => UiThread.RunAsync(async () =>
    {
        var root = NewRoot();
        Seeded waiting = null!;
        var (host, shell, repository) = await StartAsync(root, async h => waiting = await SeedAsync(h, root, "waiting.wav", JobState.Queued, withTranscript: true));
        using var _ = host;
        try
        {
            var job = Assert.Single(shell.Jobs);
            Assert.Equal(JobState.Queued, job.State);
            await shell.DeleteProjectCommand.ExecuteAsync(job);
            Assert.Equal("A project that is still being worked on cannot be deleted. Cancel it first.", shell.Status);
            Assert.Single(shell.Jobs);
            Assert.Single(await repository.ListAsync());
            Assert.True(Directory.Exists(waiting.Folder));
            await shell.Host.DisposeAsync();
        }
        finally { TestCleanup.Delete(root); }
    });

    [Fact]
    public Task DeletingWithoutNamingAProjectDeletesTheSelectedOneAndWithoutAnyDoesNothing() => UiThread.RunAsync(async () =>
    {
        var root = NewRoot();
        Seeded first = null!;
        var (host, shell, repository) = await StartAsync(root, async h => first = await SeedAsync(h, root, "a.wav", JobState.Complete, withTranscript: true));
        using var _ = host;
        try
        {
            shell.SelectedJob = null;
            await shell.DeleteProjectCommand.ExecuteAsync(null);
            Assert.Single(await repository.ListAsync());
            shell.SelectedJob = shell.Jobs[0];
            await shell.DeleteProjectCommand.ExecuteAsync(null);
            Assert.Empty(shell.Jobs);
            Assert.Empty(await repository.ListAsync());
            Assert.False(Directory.Exists(first.Folder));
            await shell.Host.DisposeAsync();
        }
        finally { TestCleanup.Delete(root); }
    });

    [Fact]
    public Task EachRowShowsTheNameTheDateAndTheStateAndItsDeleteButtonIsOnlyOnProjectsThatAreNotBeingWorkedOn() => UiThread.RunAsync(async () =>
    {
        var xaml = File.ReadAllText(Path.Combine(TranslationSources.AppFolder, "MainWindow.xaml"));
        Assert.Contains("PreviewMouseLeftButtonUp=\"ProjectClick\"", xaml);                      // a click opens the project
        Assert.Contains("local:LocalTimeConverter.Instance", xaml);                              // the date, in the person's own time
        Assert.Contains("Click=\"DeleteProjectClick\"", xaml);
        var style = File.ReadAllText(Path.Combine(TranslationSources.AppFolder, "Themes", "Controls.xaml"));
        foreach (var state in new[] { "Complete", "Failed", "Cancelled" }) Assert.Contains($"<DataTrigger Binding=\"{{Binding State}}\" Value=\"{state}\">", style);
        Assert.Contains("<Setter Property=\"Visibility\" Value=\"Collapsed\" />", style[style.IndexOf("ProjectDeleteButton", StringComparison.Ordinal)..]);
        // the Server window lists its recordings with the same button; the remote pages have theirs
        Assert.Contains("ProjectDeleteButton", File.ReadAllText(Path.Combine(TranslationSources.AppFolder, "ServerWindow.xaml")));
        var remote = File.ReadAllText(Path.Combine(TranslationSources.AppFolder, "RemoteWorkspaceView.xaml"));
        Assert.Contains("Click=\"DeleteProjectClick\"", remote);
        Assert.Contains("PreviewMouseLeftButtonUp=\"ProjectClick\"", remote);
        await Task.CompletedTask;
    });

    [Fact]
    public void ADateIsShownInTheLocalTimeOfThePerson()
    {
        var moment = new DateTimeOffset(2026, 10, 2, 17, 30, 0, TimeSpan.Zero);
        var shown = (string)LocalTimeConverter.Instance.Convert(moment, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(moment.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture), shown);
        Assert.Equal("", LocalTimeConverter.Instance.Convert(null, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void EveryTextOfTheHistoryHasAnEntryInEveryLanguage()
    {
        foreach (var text in new[]
        {
            "Click a finished project to open its transcript.", "Delete", "Delete project", "Delete this project and its transcript", "Project deleted: {0}",
            "A project that is still being worked on cannot be deleted. Cancel it first.", "Could not delete the project",
            "The project was deleted, but some files could not be removed: {0}"
        })
            foreach (var code in new[] { "hu", "de", "es", "fr" }) Assert.NotEqual(text, Loc.Load(code)[text]);
    }
}
