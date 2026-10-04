using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using TriAsr.App;
using TriAsr.Domain;
using TriAsr.Infrastructure;

namespace TriAsr.Ui.SmokeTests;

/// <summary>
/// The QR code that opens a server's web page on a phone, and what makes the Client with a Server do what Studio does: resuming a stopped recording,
/// a watch folder, a summary of the text on screen, stopping an upload.
/// </summary>
public sealed class PhoneAndParityTests
{
    private static string Text(JsonElement element, string name) => ApiTestData.Text(element, name);

    // ---- the QR code -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheCodeOfAnAddressIsASquareWithTheThreeFinderPatternsOfAQrCode()
    {
        var rows = QrCodes.Rows("https://192.168.1.20:8642");
        var size = rows.Length;
        Assert.True(size >= 21 && (size - 17) % 4 == 0, $"{size} is not the size of a QR code version");    // 21, 25, 29... modules
        Assert.All(rows, row => Assert.Equal(size, row.Length));
        foreach (var (left, top) in new[] { (0, 0), (size - 7, 0), (0, size - 7) })
            for (var y = 0; y < 7; y++)
                for (var x = 0; x < 7; x++)
                {
                    // A finder pattern: a dark ring, a light ring and a dark 3 x 3 centre.
                    var ring = Math.Max(Math.Abs(x - 3), Math.Abs(y - 3));
                    Assert.Equal(ring is 3 or <= 1 ? '1' : '0', rows[top + y][left + x]);
                }
        for (var i = 8; i < size - 8; i++) Assert.Equal(i % 2 == 0 ? '1' : '0', rows[6][i]);                 // the timing pattern
        Assert.NotEqual(rows, QrCodes.Rows("https://192.168.1.21:8642"));
    }

    [Fact]
    public void ThePictureOfTheCodeIsFrozenAndHasALightBorder()
    {
        var image = QrCodes.Image("https://10.0.0.5:8642");
        Assert.True(image.IsFrozen);                                                                           // made on any thread, shown on the window's
        Assert.Equal(QrCodes.Rows("https://10.0.0.5:8642").Length + 2 * QrCodes.QuietZone, image.Width, 3);
    }

    [Theory]
    [InlineData("192.168.1.20:8642", true, "https://192.168.1.50:8642", "https://192.168.1.20:8642")]       // the address the page came from
    [InlineData("[fd00::5]:8642", true, "https://192.168.1.50:8642", "https://[fd00::5]:8642")]
    [InlineData("127.0.0.1:8642", true, "https://192.168.1.50:8642", "https://192.168.1.50:8642")]          // works on the server only
    [InlineData("localhost:8642", true, "https://192.168.1.50:8642", "https://192.168.1.50:8642")]
    [InlineData("KITCHEN-PC:8642", true, "https://192.168.1.50:8642", "https://192.168.1.50:8642")]         // a phone may not know the computer's name
    [InlineData("[fe80::1]:8642", true, "https://192.168.1.50:8642", "https://192.168.1.50:8642")]          // link-local needs a scope a phone lacks
    [InlineData("192.168.1.20:8642", false, "https://192.168.1.50:8642", "https://192.168.1.50:8642")]      // plain http only answers its own computer
    [InlineData(null, true, "https://192.168.1.50:8642", "https://192.168.1.50:8642")]
    [InlineData("192.168.1.20:8642", true, null, null)]                                                    // the server listens for its own computer only
    public void APhoneIsGivenAnAddressItCanReach(string? host, bool secure, string? network, string? expected) =>
        Assert.Equal(expected, QrCodes.PhoneAddress(host, secure, network));

    [Fact]
    public async Task TheServerSaysWhereAPhoneOpensItsPageWithTheCode()
    {
        await using var api = await Harness.StartAsync(h => h.NetworkAddress = "https://192.168.1.50:8642");
        var info = JsonDocument.Parse(await api.Client.GetStringAsync("/v1/server")).RootElement;
        Assert.True(info.GetProperty("phone").GetBoolean());
        var phone = JsonDocument.Parse(await api.Client.GetStringAsync("/v1/phone")).RootElement;
        Assert.Equal("https://192.168.1.50:8642", Text(phone, "address"));                                     // asked on 127.0.0.1: the network address
        Assert.Equal(QrCodes.Rows("https://192.168.1.50:8642"), phone.GetProperty("rows").EnumerateArray().Select(row => row.GetString()!).ToArray());

        api.NetworkAddress = null;                                                                             // the owner chose "this computer only"
        phone = JsonDocument.Parse(await api.Client.GetStringAsync("/v1/phone")).RootElement;
        Assert.Equal(JsonValueKind.Null, phone.GetProperty("address").ValueKind);
        Assert.Equal(0, phone.GetProperty("rows").GetArrayLength());

        using var anonymous = new HttpClient { BaseAddress = api.Client.BaseAddress };
        using var refused = await anonymous.GetAsync("/v1/phone");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);                                          // the addresses of the server are for its users
    }

    [Fact]
    public async Task AnOlderServerDoesNotOfferThePhoneCode()
    {
        await using var api = await Harness.StartAsync(h => h.Phone = false);
        var info = JsonDocument.Parse(await api.Client.GetStringAsync("/v1/server")).RootElement;
        Assert.False(info.GetProperty("phone").GetBoolean());
    }

    // ---- resuming -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFailedRecordingIsResumedAndAFinishedOneIsNot()
    {
        await using var api = await Harness.StartAsync(h => h.Stages.Fail = "The engine stopped.");
        var id = Text(await api.UploadAsync("?language=de&name=a.wav"), "id");
        var failed = await api.WaitAsync(id, "failed");
        Assert.True(failed.GetProperty("resumable").GetBoolean());
        Assert.True(JsonDocument.Parse(await api.Client.GetStringAsync("/v1/server")).RootElement.GetProperty("resume").GetBoolean());

        api.Stages.Fail = null;
        using (var resumed = await api.Client.PostAsync($"/v1/transcriptions/{id}/resume", null))
        {
            Assert.Equal(HttpStatusCode.Accepted, resumed.StatusCode);
            Assert.Equal("queued", Text(JsonDocument.Parse(await resumed.Content.ReadAsStringAsync()).RootElement, "state"));
        }
        var complete = await api.WaitAsync(id, "complete");
        Assert.False(complete.GetProperty("resumable").GetBoolean());
        Assert.Equal(2, api.Stages.Started.Count(started => started == Guid.Parse(id)));

        using var again = await api.Client.PostAsync($"/v1/transcriptions/{id}/resume", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("not_resumable", await again.Content.ReadAsStringAsync());
        using var unknown = await api.Client.PostAsync($"/v1/transcriptions/{Guid.NewGuid()}/resume", null);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task ARecordingIsNotResumedWhileTheModelsOfItsLanguageAreMissing()
    {
        await using var api = await Harness.StartAsync(h => h.Stages.Fail = "The engine stopped.");
        var id = Text(await api.UploadAsync("?language=de&name=a.wav"), "id");
        await api.WaitAsync(id, "failed");
        api.MissingModels = _ => ["Whisper large-v3"];
        using var refused = await api.Client.PostAsync($"/v1/transcriptions/{id}/resume", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("models_missing", await refused.Content.ReadAsStringAsync());
        Assert.Equal("failed", Text(JsonDocument.Parse(await api.Client.GetStringAsync($"/v1/transcriptions/{id}")).RootElement, "state"));
    }

    [Fact]
    public async Task TheServerWindowCancelsAndResumesARecordingThatWasSentThroughTheApi()
    {
        await using var api = await Harness.StartAsync(h => h.Stages.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var id = Guid.Parse(Text(await api.UploadAsync("?language=de&name=a.wav"), "id"));
        await api.WaitAsync(id.ToString(), "running");
        Assert.True(await api.Service.CancelJobAsync(id));
        await api.WaitAsync(id.ToString(), "cancelled");
        Assert.False(await api.Service.CancelJobAsync(Guid.NewGuid()));                                      // not one of the API's: the window cancels its own

        api.Stages.Hold!.TrySetResult();
        Assert.True(await api.Service.ResumeJobAsync(id));
        await api.WaitAsync(id.ToString(), "complete");
        Assert.False(await api.Service.ResumeJobAsync(id));                                                   // finished: nothing to resume
        Assert.Null(await api.Service.ResumeJobAsync(Guid.NewGuid()));
    }

    // ---- the Client ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheClientResumesAFailedRecordingAndShowsTheCodeForAPhone()
    {
        var root = RemoteTests.NewRoot();
        await using var api = await Harness.StartAsync(h => { h.Stages.Fail = "The engine stopped."; h.NetworkAddress = "https://192.168.1.50:8642"; });
        var errors = new List<(string, string)>();
        var (workspace, browser) = await RemoteTests.ConnectedAsync(root, api, errors);
        await using var _ = browser;
        using var __ = workspace;
        try
        {
            var recording = Path.Combine(root, "meeting.wav");
            await File.WriteAllBytesAsync(recording, new byte[200_000]);
            workspace.SourcePath = recording;
            await workspace.SendCommand.ExecuteAsync(null);
            var row = await RemoteTests.EventuallyAsync(() => workspace.Jobs.FirstOrDefault(job => job.CanResume), "the recording fails and can be resumed");
            workspace.SelectedJob = row;
            Assert.True(workspace.ResumeSelectedCommand.CanExecute(null));
            api.Stages.Fail = null;
            await workspace.ResumeSelectedCommand.ExecuteAsync(null);
            await RemoteTests.EventuallyAsync(() => workspace.Jobs.FirstOrDefault(job => job.CanOpen), "the resumed recording completes");
            Assert.False(workspace.ResumeSelectedCommand.CanExecute(null));
            Assert.Empty(errors);

            Assert.True(workspace.CanShowPhone);
            workspace.ShowPhone = true;
            await RemoteTests.EventuallyAsync(() => workspace.PhoneCode, "the code is shown");
            Assert.Equal("https://192.168.1.50:8642", workspace.PhoneAddress);
            workspace.ShowPhone = false;

            api.NetworkAddress = null;
            workspace.ShowPhone = true;
            await RemoteTests.EventuallyAsync(() => workspace.PhoneNote.StartsWith("Only its own computer") ? workspace.PhoneNote : null, "the server says it cannot be reached");
            Assert.False(workspace.HasPhoneCode);

            // A server from before these is not asked.
            workspace.Attach(new RemoteConnection(workspace.Client!, workspace.ServerInfo! with { Phone = false, Resume = false }, browser.Servers[0]));
            Assert.False(workspace.CanShowPhone);
            Assert.False(workspace.ShowPhone);
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task TheClientSendsItsEditsBeforeTheServerSummarizes()
    {
        var root = RemoteTests.NewRoot();
        var summaries = new FakeSummaries();
        await using var api = await Harness.StartAsync(h => h.Summaries = summaries);
        var (workspace, browser) = await RemoteTests.ConnectedAsync(root, api);
        await using var _ = browser;
        using var __ = workspace;
        try
        {
            var recording = Path.Combine(root, "meeting.wav");
            await File.WriteAllBytesAsync(recording, new byte[200_000]);
            workspace.SourcePath = recording;
            await workspace.SendCommand.ExecuteAsync(null);
            var row = await RemoteTests.EventuallyAsync(() => workspace.Jobs.FirstOrDefault(job => job.CanOpen), "the job completes");
            await workspace.OpenReviewAsync(row.Id);
            workspace.Regions[1].Text = "Willkommen zur Sitzung.";
            Assert.Null(api.Saved);

            await workspace.Summary.SummarizeCommand.ExecuteAsync(null);
            Assert.NotNull(api.Saved);                                                                         // the edit reached the server first
            Assert.Equal("Willkommen zur Sitzung.", api.Saved!.Regions[1].FinalText);
            Assert.True(workspace.Summary.HasSummary);
            Assert.Single(summaries.Asked);
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task SendingARecordingCanBeStoppedAndLeavesNothingOnTheServer()
    {
        var root = RemoteTests.NewRoot();
        await using var api = await Harness.StartAsync();
        var (workspace, browser) = await RemoteTests.ConnectedAsync(root, api);
        await using var _ = browser;
        using var __ = workspace;
        try
        {
            var recording = Path.Combine(root, "long.wav");
            await File.WriteAllBytesAsync(recording, new byte[40_000_000]);
            workspace.SourcePath = recording;
            // Stopped as soon as the first part has gone, so the rest is still on its way.
            workspace.PropertyChanged += (_, change) => { if (change.PropertyName == nameof(RemoteWorkspaceViewModel.SendPercent) && workspace.SendPercent > 0) workspace.CancelSendCommand.Execute(null); };
            await workspace.SendCommand.ExecuteAsync(null);
            Assert.False(workspace.IsSending);
            Assert.Equal("Sending stopped. Nothing was left on the server.", workspace.SendStatus);
            Assert.Empty(await workspace.Client!.ListAsync(default));
            for (var i = 0; i < 100 && Directory.Exists(api.Incoming) && Directory.EnumerateFileSystemEntries(api.Incoming).Any(); i++) await Task.Delay(50);
            Assert.False(Directory.Exists(api.Incoming) && Directory.EnumerateFileSystemEntries(api.Incoming).Any());   // the part that arrived is gone
        }
        finally { TestCleanup.Delete(root); }
    }

    [Fact]
    public async Task TheClientsWatchFolderSendsNewRecordingsToTheServerAndSavesTheTranscriptNextToThem()
    {
        var root = RemoteTests.NewRoot();
        await using var api = await Harness.StartAsync();
        var (workspace, browser) = await RemoteTests.ConnectedAsync(root, api);
        await using var _ = browser;
        using var __ = workspace;
        var folder = Path.Combine(root, "inbox");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "old.wav"), new byte[2048]);                         // already there: left alone
        RemoteConnection? connection = null;
        var sent = new List<RemoteJob>();
        var errors = new List<(string, string)>();
        await using var watch = new RemoteWatchViewModel(() => connection, () => "Readable", (title, message) => { lock (errors) errors.Add((title, message)); }, action => action(),
            Path.Combine(root, "data"), job => { lock (sent) sent.Add(job); }, new WatchOptions(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(500)));
        try
        {
            watch.SetWatchFolder(folder);
            await RemoteTests.EventuallyAsync(() => watch.WatchStatus.StartsWith("Watching") ? watch.WatchStatus : null, "watching starts");
            Assert.Contains("wait until this computer is connected", watch.WatchStatus);

            // Without a server the recording waits; once there is one it is sent.
            await File.WriteAllBytesAsync(Path.Combine(folder, "meeting.wav"), new byte[2048]);
            await Task.Delay(1000);
            Assert.Empty(sent);
            connection = new RemoteConnection(workspace.Client!, workspace.ServerInfo!, browser.Servers[0]);
            watch.ServerChanged();
            var saved = await RemoteTests.EventuallyAsync(() => File.Exists(Path.Combine(folder, "meeting.txt")) ? Path.Combine(folder, "meeting.txt") : null, "the transcript is saved next to the recording");
            Assert.Contains("Guten Tag, meine Damen und Herren.", await File.ReadAllTextAsync(saved));
            Assert.Equal("meeting.wav", Assert.Single(sent).Name);
            Assert.Single(api.Repository.Jobs);                                                                  // old.wav was not sent
            Assert.Empty(errors);

            // The choices are kept for the next start.
            watch.WatchOutput = ShellViewModel.WatchAsSubtitles;
            await using var again = new RemoteWatchViewModel(() => null, () => "Readable", (_, _) => { }, action => action(), Path.Combine(root, "data"), _ => { });
            Assert.Equal((folder, true, ShellViewModel.WatchAsSubtitles), (again.WatchFolder, again.WatchEnabled, again.WatchOutput));
        }
        finally { await watch.DisposeAsync(); TestCleanup.Delete(root); }
    }
}
