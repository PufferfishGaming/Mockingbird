using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TriAsr.Application;
using TriAsr.Domain;
using TriAsr.Infrastructure;

namespace TriAsr.Ui.SmokeTests;

/// <summary><c>POST /v1/links</c> (ADR-0018): a server with a password fetches the sound of a web address for a client and transcribes it.</summary>
public sealed class LinkApiTests
{
    private static string Text(JsonElement element, string name) => ApiTestData.Text(element, name);

    private static async Task<HttpResponseMessage> PostLinkAsync(Harness api, string url, string? language = "de")
    {
        var body = language is null ? JsonSerializer.Serialize(new { url }) : JsonSerializer.Serialize(new { url, language });
        return await api.Client.PostAsync("/v1/links", new StringContent(body, Encoding.UTF8, "application/json"));
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<JsonElement> StatusAsync(Harness api, string id) => JsonDocument.Parse(await api.Client.GetStringAsync($"/v1/transcriptions/{id}")).RootElement.Clone();

    private static async Task<JsonElement> UntilAsync(Harness api, string id, Func<JsonElement, bool> done, string what)
    {
        for (var i = 0; i < 200; i++)
        {
            var status = await StatusAsync(api, id);
            if (done(status)) return status;
            await Task.Delay(50);
        }
        throw new TimeoutException(what);
    }

    [Fact]
    public async Task ALinkIsAcceptedAtOnceListedAsDownloadingAndThenTranscribedUnderTheSameId()
    {
        var links = new FakeLinks { Hold = new TaskCompletionSource(), Progress = [40] };
        await using var api = await Harness.StartAsync(h => h.Links = links);
        using var response = await PostLinkAsync(api, "https://media.example/talk.mp3");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await JsonOf(response);
        var id = Text(accepted, "id");
        Assert.Equal($"/v1/transcriptions/{id}", response.Headers.Location!.OriginalString);
        Assert.Equal("running", Text(accepted, "state"));
        Assert.Equal("Downloading the link", Text(accepted, "stage"));
        Assert.Equal("de", Text(accepted, "language"));
        Assert.Equal("media.example", Text(accepted, "name"));                                       // until the file has arrived the job is named after the site

        var downloading = await UntilAsync(api, id, status => status.GetProperty("percent").GetInt32() == 40, "the progress of the download is shown");
        Assert.Equal("running", Text(downloading, "state"));
        Assert.Equal("Downloading the link", Text(downloading, "stage"));
        var list = JsonDocument.Parse(await api.Client.GetStringAsync("/v1/transcriptions")).RootElement.GetProperty("data");
        Assert.Equal(id, Text(Assert.Single(list.EnumerateArray()), "id"));
        Assert.Empty(api.Repository.Jobs);                                                           // nothing is stored for a recording that has not arrived

        var call = Assert.Single(links.Calls);
        Assert.Equal("https://media.example/talk.mp3", call.Link.AbsoluteUri);
        Assert.False(call.Options.AllowPrivateNetwork);                                              // a server never follows a client into its own network
        Assert.StartsWith(api.Incoming + Path.DirectorySeparatorChar, call.Folder);

        links.Hold.SetResult();
        var done = await api.WaitAsync(id);
        Assert.Equal("Fetched Talk.mp3", Text(done, "name"));
        var stored = Assert.Single(api.Repository.Jobs.Values);
        Assert.Equal(Guid.Parse(id), stored.Id);
        Assert.Equal(Path.Combine(call.Folder, "Fetched Talk.mp3"), stored.SourcePath);
        Assert.Contains("Guten Tag", await api.Client.GetStringAsync($"/v1/transcriptions/{id}/transcript?format=txt"));
        Assert.Equal(id, Text(Assert.Single(JsonDocument.Parse(await api.Client.GetStringAsync("/v1/transcriptions")).RootElement.GetProperty("data").EnumerateArray()), "id"));
    }

    [Fact]
    public async Task OtherRecordingsAreNotHeldUpWhileALinkDownloads()
    {
        var links = new FakeLinks { Hold = new TaskCompletionSource() };
        await using var api = await Harness.StartAsync(h => h.Links = links);
        using var response = await PostLinkAsync(api, "https://media.example/slow.mp3");
        var id = Text(await JsonOf(response), "id");
        var upload = await api.UploadAsync();
        await api.WaitAsync(Text(upload, "id"));
        Assert.Equal("running", Text(await StatusAsync(api, id), "state"));
        links.Hold.SetResult();
        await api.WaitAsync(id);
    }

    [Fact]
    public async Task ALinkNeedsAPasswordOnTheServerBecauseTheServerIsTheOneThatFetches()
    {
        var links = new FakeLinks();
        await using var api = await Harness.StartAsync(h => { h.Links = links; h.CurrentKey = ""; });
        using var response = await PostLinkAsync(api, "https://media.example/talk.mp3");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("links_need_password", Text((await JsonOf(response)).GetProperty("error"), "code"));
        Assert.Empty(links.Calls);

        using var anonymous = new HttpClient { BaseAddress = api.Client.BaseAddress };
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.PostAsync("/v1/links", new StringContent("{\"url\":\"https://a.example/x.mp3\"}", Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Fact]
    public async Task WithoutAPasswordGuessedTheRequestIsRefusedAndAServerWithoutLinkSupportSaysSo()
    {
        var links = new FakeLinks();
        await using var api = await Harness.StartAsync(h => h.Links = links);
        using var anonymous = new HttpClient { BaseAddress = api.Client.BaseAddress };
        using var refused = await anonymous.PostAsync("/v1/links", new StringContent("{\"url\":\"https://a.example/x.mp3\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Empty(links.Calls);

        await using var plain = await Harness.StartAsync();
        using var none = await PostLinkAsync(plain, "https://media.example/talk.mp3");
        Assert.Equal(HttpStatusCode.NotImplemented, none.StatusCode);
        Assert.Equal("links_unavailable", Text((await JsonOf(none)).GetProperty("error"), "code"));
    }

    [Theory]
    [InlineData("ftp://media.example/talk.mp3", LinkMessages.OnlyWeb)]
    [InlineData("not a web address", LinkMessages.NotAddress)]
    [InlineData("", LinkMessages.Paste)]
    [InlineData("https://user:secret@media.example/talk.mp3", LinkMessages.HasUserInfo)]
    [InlineData("http://127.0.0.1:9/talk.mp3", LinkMessages.PrivateNetwork)]
    [InlineData("http://localhost/talk.mp3", LinkMessages.PrivateNetwork)]
    [InlineData("http://192.168.1.10/talk.mp3", LinkMessages.PrivateNetwork)]
    [InlineData("http://169.254.169.254/latest/meta-data/", LinkMessages.PrivateNetwork)]
    [InlineData("http://[::1]/talk.mp3", LinkMessages.PrivateNetwork)]
    public async Task AnAddressThatCannotBeUsedIsRefusedBeforeAnyJobExistsAndSaysWhy(string url, string reason)
    {
        var links = new FakeLinks();
        await using var api = await Harness.StartAsync(h => h.Links = links);
        using var response = await PostLinkAsync(api, url);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await JsonOf(response)).GetProperty("error");
        Assert.Equal("invalid_link", Text(error, "code"));
        Assert.Equal(reason, Text(error, "message"));
        Assert.Empty(links.Calls);
        Assert.Empty(JsonDocument.Parse(await api.Client.GetStringAsync("/v1/transcriptions")).RootElement.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task ABadRequestIsRefusedForItsBodyItsLanguageOrTheModelsTheServerLacks()
    {
        var links = new FakeLinks();
        await using var api = await Harness.StartAsync(h => h.Links = links);
        using var notJson = await api.Client.PostAsync("/v1/links", new StringContent("this is not json", Encoding.UTF8, "application/json"));
        Assert.Equal("bad_json", Text((await JsonOf(notJson)).GetProperty("error"), "code"));
        using var nothing = await api.Client.PostAsync("/v1/links", new StringContent("null", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, nothing.StatusCode);
        using var huge = await api.Client.PostAsync("/v1/links", new StringContent("{\"url\":\"https://a.example/" + new string('a', 20_000) + "\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);
        using var language = await PostLinkAsync(api, "https://media.example/talk.mp3", "klingon");
        Assert.Equal("unsupported_language", Text((await JsonOf(language)).GetProperty("error"), "code"));
        using var get = await api.Client.GetAsync("/v1/links");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);

        api.MissingModels = _ => ["Whisper large-v3"];
        using var models = await PostLinkAsync(api, "https://media.example/talk.mp3");
        Assert.Equal(HttpStatusCode.Conflict, models.StatusCode);
        Assert.Equal("models_missing", Text((await JsonOf(models)).GetProperty("error"), "code"));
        Assert.Empty(links.Calls);
    }

    [Fact]
    public async Task ALanguageIsOptionalAndDefaultsToAutomaticDetection()
    {
        var links = new FakeLinks();
        await using var api = await Harness.StartAsync(h => h.Links = links);
        using var response = await PostLinkAsync(api, "media.example/talk.mp3", language: null);       // and the scheme may be left out
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await JsonOf(response);
        Assert.Equal("auto", Text(accepted, "language"));
        await api.WaitAsync(Text(accepted, "id"));
        Assert.Equal("https://media.example/talk.mp3", Assert.Single(links.Calls).Link.AbsoluteUri);
    }

    [Fact]
    public async Task ALinkThatFailsEndsAsAFailedRecordingWithTheReasonAndLeavesNothingOnTheDisk()
    {
        var links = new FakeLinks { Fail = _ => new LinkException(LinkMessages.NoSound) };
        await using var api = await Harness.StartAsync(h => h.Links = links);
        using var response = await PostLinkAsync(api, "https://media.example/page");
        var id = Text(await JsonOf(response), "id");
        var failed = await api.WaitAsync(id, "failed");
        Assert.Equal(LinkMessages.NoSound, Text(failed, "error"));
        Assert.Equal(0, failed.GetProperty("percent").GetInt32());
        Assert.Empty(api.Repository.Jobs);
        Assert.True(!Directory.Exists(api.Incoming) || Directory.GetFileSystemEntries(api.Incoming).Length == 0);

        using var transcript = await api.Client.GetAsync($"/v1/transcriptions/{id}/transcript");
        Assert.Equal(HttpStatusCode.Conflict, transcript.StatusCode);                                // a failed recording has no transcript
        links.Fail = _ => new IOException("The disk is full.");
        var second = Text(await JsonOf(await PostLinkAsync(api, "https://media.example/other")), "id");
        Assert.Equal("The link could not be downloaded.", Text(await api.WaitAsync(second, "failed"), "error"));   // and an error that is not ours is never shown as it is
    }

    [Fact]
    public async Task ALinkCanBeCancelledWhileItDownloads()
    {
        var links = new FakeLinks { Hold = new TaskCompletionSource() };
        await using var api = await Harness.StartAsync(h => h.Links = links);
        var id = Text(await JsonOf(await PostLinkAsync(api, "https://media.example/long.mp3")), "id");
        await UntilAsync(api, id, _ => links.Calls.Count == 1, "the download starts");
        using var cancelled = await api.Client.PostAsync($"/v1/transcriptions/{id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var status = await api.WaitAsync(id, "cancelled");
        Assert.Equal("cancelled", Text(status, "state"));
        Assert.True(links.LastToken.IsCancellationRequested);
        Assert.Empty(api.Repository.Jobs);
        Assert.True(!Directory.Exists(api.Incoming) || Directory.GetFileSystemEntries(api.Incoming).Length == 0);
    }

    [Fact]
    public async Task TheServerSaysWhetherItFetchesLinksAndWhetherItCanFetchPages()
    {
        var links = new FakeLinks();
        await using var api = await Harness.StartAsync(h => h.Links = links);
        var info = JsonDocument.Parse(await api.Client.GetStringAsync("/v1/server")).RootElement;
        Assert.True(info.GetProperty("linksEnabled").GetBoolean());
        Assert.False(info.GetProperty("linkPages").GetBoolean());                                    // only links to files until the helper is installed
        links.PagesReady = true;
        info = JsonDocument.Parse(await api.Client.GetStringAsync("/v1/server")).RootElement;
        Assert.True(info.GetProperty("linkPages").GetBoolean());

        api.CurrentKey = "";
        info = JsonDocument.Parse(await api.Client.GetStringAsync("/v1/server")).RootElement;
        Assert.False(info.GetProperty("linksEnabled").GetBoolean());                                 // no password, no links

        await using var plain = await Harness.StartAsync();
        info = JsonDocument.Parse(await plain.Client.GetStringAsync("/v1/server")).RootElement;
        Assert.False(info.GetProperty("linksEnabled").GetBoolean());
        Assert.False(info.GetProperty("linkPages").GetBoolean());
    }

    [Fact]
    public async Task TheClientLibraryAsksForALinkAndReadsTheServersAnswer()
    {
        var links = new FakeLinks();
        await using var api = await Harness.StartAsync(h => h.Links = links);
        using var client = new RemoteServerClient(api.Client.BaseAddress!, ApiTestData.Key, null);
        var job = await client.SendLinkAsync("https://media.example/talk.mp3", "de", default);
        Assert.Equal("running", job.State);
        Assert.Equal("Downloading the link", job.Stage);
        await api.WaitAsync(job.Id.ToString());
        var info = await client.InfoAsync(default);
        Assert.True(info.LinksEnabled);
        var refused = await Assert.ThrowsAsync<RemoteException>(() => client.SendLinkAsync("ftp://media.example/talk.mp3", "de", default));
        Assert.Equal("invalid_link", refused.Code);
        Assert.Equal(LinkMessages.OnlyWeb, refused.Message);
    }

    [Fact]
    public async Task EveryServerAnswerStillReadsWithoutTheLinkFieldsForAnOlderServer()
    {
        await Task.CompletedTask;
        var older = JsonSerializer.Deserialize<RemoteServerInfo>("{\"name\":\"Old\",\"edition\":\"Studio\",\"version\":\"0.1.19\",\"encrypted\":true,\"passwordRequired\":true,\"modelsReady\":true,\"missingModels\":[],\"busy\":false,\"queued\":0}",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.False(older.LinksEnabled);
        Assert.False(older.LinkPages);
    }
}
