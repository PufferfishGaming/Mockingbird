using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using TriAsr.Domain;
using TriAsr.Infrastructure;

namespace TriAsr.Ui.SmokeTests;

/// <summary><c>DELETE /v1/transcriptions/{id}</c>: a finished recording goes with its transcript, edits and the uploaded copy; one that is being worked on stays.</summary>
public sealed class ProjectApiTests
{
    private static string Text(JsonElement element, string name) => ApiTestData.Text(element, name);

    private static int FileCount(string folder) => Directory.Exists(folder) ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length : 0;

    [Fact]
    public async Task AFinishedRecordingIsDeletedWithItsFilesAndTheUploadedCopyAndThenIsGoneFromEveryList()
    {
        await using var api = await Harness.StartAsync();
        var deleted = new List<Guid>();
        api.Service.JobDeletedByApi += (_, id) => deleted.Add(id);
        var first = Text(await api.UploadAsync("?language=de&name=first.wav"), "id");
        var second = Text(await api.UploadAsync("?language=de&name=second.wav"), "id");
        await api.WaitAsync(first); await api.WaitAsync(second);
        var firstJob = api.Repository.Jobs[Guid.Parse(first)];
        var workingFolder = Path.Combine(api.Root, "Jobs", Guid.Parse(first).ToString("N"));
        Directory.CreateDirectory(workingFolder); File.WriteAllText(Path.Combine(workingFolder, "final.json"), "{}");
        Assert.True(File.Exists(firstJob.SourcePath));

        using var response = await api.Client.DeleteAsync($"/v1/transcriptions/{first}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(first, Text(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement, "deleted"));

        Assert.False(File.Exists(firstJob.SourcePath));                                      // the uploaded copy
        Assert.False(Directory.Exists(Path.GetDirectoryName(firstJob.SourcePath)!));
        Assert.False(Directory.Exists(workingFolder));
        Assert.DoesNotContain(Guid.Parse(first), api.Repository.Jobs.Keys);
        Assert.Contains(Guid.Parse(second), api.Repository.Jobs.Keys);                       // the other recording is untouched
        Assert.True(File.Exists(api.Repository.Jobs[Guid.Parse(second)].SourcePath));
        Assert.Equal([Guid.Parse(first)], deleted);

        using var gone = await api.Client.GetAsync($"/v1/transcriptions/{first}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using var transcript = await api.Client.GetAsync($"/v1/transcriptions/{first}/transcript");
        Assert.Equal(HttpStatusCode.NotFound, transcript.StatusCode);
        var list = JsonDocument.Parse(await api.Client.GetStringAsync("/v1/transcriptions")).RootElement.GetProperty("data");
        Assert.Equal(second, Text(Assert.Single(list.EnumerateArray()), "id"));
        using var again = await api.Client.DeleteAsync($"/v1/transcriptions/{first}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task ARecordingThatIsBeingWorkedOnIsRefusedUntilItIsCancelled()
    {
        await using var api = await Harness.StartAsync(h => h.Stages.Hold = new TaskCompletionSource());
        var id = Text(await api.UploadAsync(), "id");
        await api.WaitAsync(id, "running");
        using var refused = await api.Client.DeleteAsync($"/v1/transcriptions/{id}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("still_running", Text(JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("error"), "code"));
        Assert.Contains(Guid.Parse(id), api.Repository.Jobs.Keys);
        Assert.True(File.Exists(api.Repository.Jobs[Guid.Parse(id)].SourcePath));

        using var cancelled = await api.Client.PostAsync($"/v1/transcriptions/{id}/cancel", null);
        await api.WaitAsync(id, "cancelled");
        for (var i = 0; i < 100; i++)                                                         // the runner lets go a moment after the state changes
        {
            using var attempt = await api.Client.DeleteAsync($"/v1/transcriptions/{id}");
            if (attempt.StatusCode == HttpStatusCode.OK) return;
            Assert.Equal(HttpStatusCode.Conflict, attempt.StatusCode);
            await Task.Delay(50);
        }
        Assert.Fail("A cancelled recording could not be deleted.");
    }

    [Fact]
    public async Task AFailedRecordingCanBeDeletedToo()
    {
        await using var api = await Harness.StartAsync(h => h.Stages.Fail = "The engine crashed on {source}.");
        var id = Text(await api.UploadAsync(), "id");
        var failed = await api.WaitAsync(id, "failed");
        Assert.DoesNotContain(api.Incoming, Text(failed, "error"));
        using var response = await api.Client.DeleteAsync($"/v1/transcriptions/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, FileCount(api.Incoming));
    }

    [Fact]
    public async Task DeletingNeedsThePasswordLikeEverythingElseAndOnlyConcernsRecordingsSentThroughTheApi()
    {
        await using var api = await Harness.StartAsync();
        var id = Text(await api.UploadAsync(), "id");
        await api.WaitAsync(id);
        using var anonymous = new HttpClient { BaseAddress = api.Client.BaseAddress };
        using var refused = await anonymous.DeleteAsync($"/v1/transcriptions/{id}");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Contains(Guid.Parse(id), api.Repository.Jobs.Keys);

        // a project that was made in the window is not known to the API, so it cannot be deleted through it
        var own = new TranscriptionJob(Guid.NewGuid(), Path.Combine(api.Root, "mine.wav"), "de", JobState.Complete, DateTimeOffset.UtcNow);
        await api.Repository.SaveAsync(own);
        using var unknown = await api.Client.DeleteAsync($"/v1/transcriptions/{own.Id}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains(own.Id, api.Repository.Jobs.Keys);
        using var notAnId = await api.Client.DeleteAsync("/v1/transcriptions/not-an-id");
        Assert.Equal(HttpStatusCode.NotFound, notAnId.StatusCode);
        using var wrongMethod = await api.Client.PutAsync($"/v1/transcriptions/{id}", new StringContent(""));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        Assert.Equal(["GET", "DELETE"], wrongMethod.Content.Headers.Allow);
    }

    [Fact]
    public async Task TheClientLibraryDeletesARecordingAndReportsOneThatCannotBeDeleted()
    {
        await using var api = await Harness.StartAsync();
        using var client = new RemoteServerClient(api.Client.BaseAddress!, ApiTestData.Key, null);
        var id = Text(await api.UploadAsync(), "id");
        await api.WaitAsync(id);
        await client.DeleteAsync(Guid.Parse(id), default);
        Assert.Empty(await client.ListAsync(default));
        var missing = await Assert.ThrowsAsync<RemoteException>(() => client.DeleteAsync(Guid.Parse(id), default));
        Assert.Equal(404, missing.Status);
    }

    [Fact]
    public async Task ALinkThatIsStillBeingFetchedCannotBeDeleted()
    {
        var links = new FakeLinks { Hold = new TaskCompletionSource() };
        await using var api = await Harness.StartAsync(h => h.Links = links);
        using var started = await api.Client.PostAsync("/v1/links", new StringContent("{\"url\":\"https://media.example/talk.mp3\"}", System.Text.Encoding.UTF8, "application/json"));
        var id = Text(JsonDocument.Parse(await started.Content.ReadAsStringAsync()).RootElement, "id");
        using var refused = await api.Client.DeleteAsync($"/v1/transcriptions/{id}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        links.Hold.SetResult();
        await api.WaitAsync(id);
    }
}
