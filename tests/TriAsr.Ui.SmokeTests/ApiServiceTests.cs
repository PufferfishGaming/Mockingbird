using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TriAsr.App;
using TriAsr.Application;
using TriAsr.Domain;
using TriAsr.Infrastructure;

namespace TriAsr.Ui.SmokeTests;

/// <summary>The whole API over a real socket, with fake engines: what a client sees for uploads, progress, transcripts, errors, keys and limits.</summary>
public sealed class ApiServiceTests
{

    private const string Key = ApiTestData.Key;
    private static FinalTranscript Transcript(Guid id, bool native) => ApiTestData.Transcript(id, native);
    private static string Text(JsonElement element, string name) => ApiTestData.Text(element, name);

    // ---- keys ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnlyTheHealthCheckAndTheStartPageNeedNoKey()
    {
        await using var api = await Harness.StartAsync();
        using var anonymous = new HttpClient { BaseAddress = api.Client.BaseAddress };
        Assert.Contains("\"status\":\"ok\"", await anonymous.GetStringAsync("/v1/health"));
        Assert.Contains("local transcription API", await anonymous.GetStringAsync("/"));
        foreach (var path in new[] { "/v1/languages", "/v1/models", "/v1/transcriptions", $"/v1/transcriptions/{Guid.NewGuid()}", "/v1/nothing-here" })
        {
            using var response = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        }
    }

    [Fact]
    public async Task TheKeyCanComeAsABearerTokenOrAnApiKeyHeaderButNeverInTheAddress()
    {
        await using var api = await Harness.StartAsync();
        using var plain = new HttpClient { BaseAddress = api.Client.BaseAddress };
        plain.DefaultRequestHeaders.Add("X-Api-Key", Key);
        Assert.Equal(HttpStatusCode.OK, (await plain.GetAsync("/v1/languages")).StatusCode);
        using var lower = new HttpClient { BaseAddress = api.Client.BaseAddress };
        lower.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "bearer " + Key);
        Assert.Equal(HttpStatusCode.OK, (await lower.GetAsync("/v1/languages")).StatusCode);
        using var wrong = new HttpClient { BaseAddress = api.Client.BaseAddress };
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key + "x");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/v1/languages")).StatusCode);
        using var query = new HttpClient { BaseAddress = api.Client.BaseAddress };
        Assert.Equal(HttpStatusCode.Unauthorized, (await query.GetAsync($"/v1/languages?key={Key}")).StatusCode);
    }

    [Fact]
    public async Task ANewKeyWorksAtOnceAndTheOldOneStops()
    {
        await using var api = await Harness.StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await api.Client.GetAsync("/v1/languages")).StatusCode);
        api.CurrentKey = AuthThrottle.NewPassword();   // what "New key" in Settings does
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client.GetAsync("/v1/languages")).StatusCode);
        using var renewed = new HttpClient { BaseAddress = api.Client.BaseAddress };
        renewed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", api.CurrentKey);
        Assert.Equal(HttpStatusCode.OK, (await renewed.GetAsync("/v1/languages")).StatusCode);
    }

    [Fact]
    public async Task GuessingKeysIsStoppedAfterAFewWrongOnes()
    {
        await using var api = await Harness.StartAsync();
        using var guesser = new HttpClient { BaseAddress = api.Client.BaseAddress };
        guesser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "mbk-wrong");
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 10; i++) statuses.Add((await guesser.GetAsync("/v1/languages")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, statuses[0]);
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await api.Client.GetAsync("/v1/languages")).StatusCode); // even the right key waits: the address is locked out
    }

    // ---- uploads and progress -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARecordingIsUploadedRunsAndItsTranscriptIsFetchedInEveryFormat()
    {
        await using var api = await Harness.StartAsync();
        var started = await api.UploadAsync();
        var id = Text(started, "id");
        Assert.Equal("meeting.wav", Text(started, "name"));
        Assert.Equal("de", Text(started, "language"));
        var done = await api.WaitAsync(id);
        Assert.Equal(100, done.GetProperty("percent").GetInt32());

        var json = JsonDocument.Parse(await api.Client.GetStringAsync($"/v1/transcriptions/{id}/transcript")).RootElement;
        Assert.Equal("Guten Tag, meine Damen und Herren. Willkommen   zur  Sitzung .", Text(json, "text"));
        Assert.Equal(2, json.GetProperty("segments").GetArrayLength());
        Assert.False(json.GetProperty("segments")[0].GetProperty("needsListening").GetBoolean());
        Assert.True(json.GetProperty("segments")[1].GetProperty("needsListening").GetBoolean());
        Assert.Equal(2500, json.GetProperty("segments")[0].GetProperty("endMs").GetInt64());

        var txt = await api.Client.GetStringAsync($"/v1/transcriptions/{id}/transcript?format=txt");
        Assert.Contains("Guten Tag, meine Damen und Herren.", txt);
        Assert.Contains("Willkommen   zur  Sitzung .", txt);                       // strict: as saved
        Assert.Contains("Willkommen zur Sitzung.", await api.Client.GetStringAsync($"/v1/transcriptions/{id}/transcript?format=txt&mode=readable"));
        Assert.Contains("00:00:00,000 --> 00:00:02,500", await api.Client.GetStringAsync($"/v1/transcriptions/{id}/transcript?format=srt"));
        Assert.StartsWith("WEBVTT", await api.Client.GetStringAsync($"/v1/transcriptions/{id}/transcript?format=vtt"));
        Assert.StartsWith("startMs,endMs,finalText", await api.Client.GetStringAsync($"/v1/transcriptions/{id}/transcript?format=csv"));
        Assert.StartsWith("# Transcript", await api.Client.GetStringAsync($"/v1/transcriptions/{id}/transcript?format=md"));
        Assert.Contains("\"WhisperText\"", await api.Client.GetStringAsync($"/v1/transcriptions/{id}/transcript?format=full-json"));
        var docx = await api.Client.GetByteArrayAsync($"/v1/transcriptions/{id}/transcript?format=docx");
        Assert.Equal((byte)'P', docx[0]); Assert.Equal((byte)'K', docx[1]);       // a zip

        using var bad = await api.Client.GetAsync($"/v1/transcriptions/{id}/transcript?format=pdf");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var badMode = await api.Client.GetAsync($"/v1/transcriptions/{id}/transcript?mode=creative");
        Assert.Equal(HttpStatusCode.BadRequest, badMode.StatusCode);
    }

    [Fact]
    public async Task TheUploadIsKeptInTheApiFolderUnderASafeName()
    {
        await using var api = await Harness.StartAsync();
        var started = await api.UploadAsync("?name=" + Uri.EscapeDataString(@"..\..\Windows\evil:name?.wav"), [1, 2, 3, 4]);
        var id = Text(started, "id");
        await api.WaitAsync(id);
        var job = api.Repository.Jobs[Guid.Parse(id)];
        Assert.StartsWith(Path.GetFullPath(api.Incoming) + Path.DirectorySeparatorChar, job.SourcePath);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(job.SourcePath));
        Assert.DoesNotContain("..", Path.GetRelativePath(api.Incoming, job.SourcePath));
        Assert.EndsWith(".wav", job.SourcePath);

        var reserved = await api.UploadAsync("?name=CON.wav");
        Assert.NotEqual("CON.wav", Text(reserved, "name"));
        var unnamed = await api.UploadAsync("");
        Assert.Equal("recording", Text(unnamed, "name"));
    }

    [Fact]
    public async Task AnEmptyUploadAnUnknownLanguageAndAMissingModelAreRefusedWithAReason()
    {
        string[] missing = [];
        await using var api = await Harness.StartAsync(configure: h => h.MissingModels = _ => missing);
        using var empty = await api.Client.PostAsync("/v1/transcriptions?language=de", new ByteArrayContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("empty_upload", await empty.Content.ReadAsStringAsync());
        using var language = await api.Client.PostAsync("/v1/transcriptions?language=klingon", new ByteArrayContent([1]));
        Assert.Equal(HttpStatusCode.BadRequest, language.StatusCode);
        Assert.Contains("unsupported_language", await language.Content.ReadAsStringAsync());
        using var multipart = await api.Client.PostAsync("/v1/transcriptions", new MultipartFormDataContent { { new ByteArrayContent([1]), "file", "x.wav" } });
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, multipart.StatusCode);

        var api2 = await Harness.StartAsync(configure: h => h.MissingModels = _ => ["Whisper large-v3", "Canary 1B"]);
        await using (api2)
        {
            using var response = await api2.Client.PostAsync("/v1/transcriptions?language=de", new ByteArrayContent([1, 2, 3]));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("models_missing", body.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(2, body.GetProperty("missing").GetArrayLength());
            Assert.Empty(Directory.Exists(api2.Incoming) ? Directory.GetDirectories(api2.Incoming) : []);   // nothing is left behind
        }
    }

    [Fact]
    public async Task RecordingsRunOneAtATimeInTheOrderTheyArrived()
    {
        await using var api = await Harness.StartAsync(configure: h => h.Stages.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var first = Text(await api.UploadAsync("?name=a.wav"), "id");
        var second = Text(await api.UploadAsync("?name=b.wav"), "id");
        await api.WaitAsync(first, "running");
        Assert.Equal("queued", Text(JsonDocument.Parse(await api.Client.GetStringAsync($"/v1/transcriptions/{second}")).RootElement, "state"));
        Assert.Single(api.Stages.Started);
        var list = JsonDocument.Parse(await api.Client.GetStringAsync("/v1/transcriptions")).RootElement.GetProperty("data");
        Assert.Equal(2, list.GetArrayLength());
        Assert.Equal(second, Text(list[0], "id"));   // newest first
        api.Stages.Hold!.SetResult();
        await api.WaitAsync(first);
        await api.WaitAsync(second);
        Assert.Equal([Guid.Parse(first), Guid.Parse(second)], api.Stages.Started);
        lock (api.Busy) { Assert.True(api.Busy.First()); Assert.False(api.Busy.Last()); }
    }

    [Fact]
    public async Task ARecordingThatIsWaitingOrRunningCanBeCancelled()
    {
        await using var api = await Harness.StartAsync(configure: h => h.Stages.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var running = Text(await api.UploadAsync("?name=a.wav"), "id");
        var waiting = Text(await api.UploadAsync("?name=b.wav"), "id");
        await api.WaitAsync(running, "running");
        using var cancelWaiting = await api.Client.PostAsync($"/v1/transcriptions/{waiting}/cancel", null);
        Assert.Equal("cancelled", Text(JsonDocument.Parse(await cancelWaiting.Content.ReadAsStringAsync()).RootElement, "state"));
        using var cancelRunning = await api.Client.PostAsync($"/v1/transcriptions/{running}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancelRunning.StatusCode);
        await api.WaitAsync(running, "cancelled");
        await Task.Delay(300);
        Assert.Single(api.Stages.Started);                                   // the cancelled one never ran
        using var late = await api.Client.GetAsync($"/v1/transcriptions/{running}/transcript");
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
    }

    [Fact]
    public async Task AFailureIsReportedWithoutTheLocationOfTheUploadOnThisComputer()
    {
        await using var api = await Harness.StartAsync(configure: h => h.Stages.Fail = "FFmpeg could not read {source}: Invalid data");
        var id = Text(await api.UploadAsync("?name=broken.mp3"), "id");
        var failed = await api.WaitAsync(id, "failed");
        var error = Text(failed, "error");
        Assert.Contains("<uploads>", error);
        Assert.DoesNotContain(api.Incoming, error);
        Assert.DoesNotContain(Environment.UserName, error.Replace("Invalid data", ""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WaitHoldsTheAnswerUntilTheRecordingIsDone()
    {
        await using var api = await Harness.StartAsync();
        var id = Text(await api.UploadAsync(), "id");
        var status = JsonDocument.Parse(await api.Client.GetStringAsync($"/v1/transcriptions/{id}?wait=30")).RootElement;
        Assert.Equal("complete", Text(status, "state"));
    }

    [Fact]
    public async Task OnlyRecordingsSentThroughTheApiAreVisibleToIt()
    {
        var outside = Guid.NewGuid();
        await using var api = await Harness.StartAsync(before: async h =>
            await h.Repository.SaveAsync(new TranscriptionJob(outside, @"C:\Users\Anna\Recordings\private.wav", "de", JobState.Complete, DateTimeOffset.UtcNow)));
        using var response = await api.Client.GetAsync($"/v1/transcriptions/{outside}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, JsonDocument.Parse(await api.Client.GetStringAsync("/v1/transcriptions")).RootElement.GetProperty("data").GetArrayLength());
        using var transcript = await api.Client.GetAsync($"/v1/transcriptions/{outside}/transcript");
        Assert.Equal(HttpStatusCode.NotFound, transcript.StatusCode);
    }

    [Fact]
    public async Task AWaitingRecordingFromAnEarlierRunIsPickedUpAgain()
    {
        var earlier = Guid.NewGuid();
        string? source = null;
        await using var api = await Harness.StartAsync(before: async h =>
        {
            Directory.CreateDirectory(h.Incoming);
            var folder = Path.Combine(h.Incoming, earlier.ToString("N"));
            Directory.CreateDirectory(folder);
            source = Path.GetFullPath(Path.Combine(folder, "left-over.wav"));
            await File.WriteAllBytesAsync(source, [1, 2, 3]);
            await h.Repository.SaveAsync(new TranscriptionJob(earlier, source, "auto", JobState.Queued, DateTimeOffset.UtcNow));
        });
        await api.WaitAsync(earlier.ToString());
        Assert.Contains(earlier, api.Stages.Started);
    }

    [Fact]
    public async Task UnknownAddressesAndWrongMethodsGetTheRightAnswers()
    {
        await using var api = await Harness.StartAsync();
        using var unknown = await api.Client.GetAsync("/v1/unknown");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var delete = await api.Client.DeleteAsync("/v1/transcriptions");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, delete.StatusCode);
        Assert.Equal("GET, POST", string.Join(", ", delete.Content.Headers.Allow));
        using var missing = await api.Client.GetAsync($"/v1/transcriptions/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var notAnId = await api.Client.GetAsync("/v1/transcriptions/not-an-id");
        Assert.Equal(HttpStatusCode.NotFound, notAnId.StatusCode);
        var languages = JsonDocument.Parse(await api.Client.GetStringAsync("/v1/languages")).RootElement.GetProperty("data");
        Assert.Equal("auto", Text(languages[0], "code"));
        Assert.Contains(languages.EnumerateArray(), item => Text(item, "code") == "hu" && item.GetProperty("secondEngine").GetBoolean());
        Assert.Contains(languages.EnumerateArray(), item => Text(item, "code") == "ja" && !item.GetProperty("secondEngine").GetBoolean());
    }

    [Fact]
    public async Task SubtitlesAreRefusedForATranscriptWithoutNativeTimestamps()
    {
        await using var api = await Harness.StartAsync(configure: h => h.Native = false);
        var id = Text(await api.UploadAsync(), "id");
        await api.WaitAsync(id);
        using var srt = await api.Client.GetAsync($"/v1/transcriptions/{id}/transcript?format=srt");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, srt.StatusCode);
        Assert.Contains("subtitles_unavailable", await srt.Content.ReadAsStringAsync());
        var json = JsonDocument.Parse(await api.Client.GetStringAsync($"/v1/transcriptions/{id}/transcript")).RootElement;
        Assert.False(json.GetProperty("hasTimestamps").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("segments")[0].GetProperty("startMs").ValueKind);
        Assert.Contains("Guten Tag", await api.Client.GetStringAsync($"/v1/transcriptions/{id}/transcript?format=txt"));
    }

    // ---- the OpenAI-style endpoint --------------------------------------------------------------------------------------------------

    private static MultipartFormDataContent Form(string? language = "de", string? format = null, bool file = true, bool fileFirst = false)
    {
        var form = new MultipartFormDataContent("----test" + Guid.NewGuid().ToString("N"));
        void File_() { if (file) { var content = new ByteArrayContent(new byte[4096]); content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav"); form.Add(content, "file", "talk.wav"); } }
        if (fileFirst) File_();
        form.Add(new StringContent("whisper-1"), "model");
        if (language is not null) form.Add(new StringContent(language), "language");
        if (format is not null) form.Add(new StringContent(format), "response_format");
        if (!fileFirst) File_();
        return form;
    }

    [Fact]
    public async Task TheOpenAiStyleEndpointAnswersWhenTheTranscriptIsReadyInTheFormatAsked()
    {
        await using var api = await Harness.StartAsync();
        var plain = JsonDocument.Parse(await (await api.Client.PostAsync("/v1/audio/transcriptions", Form())).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Guten Tag, meine Damen und Herren. Willkommen   zur  Sitzung .", Text(plain, "text"));

        var fileFirst = JsonDocument.Parse(await (await api.Client.PostAsync("/v1/audio/transcriptions", Form(fileFirst: true))).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(Text(plain, "text"), Text(fileFirst, "text"));

        Assert.Equal("Guten Tag, meine Damen und Herren. Willkommen   zur  Sitzung .\n", await (await api.Client.PostAsync("/v1/audio/transcriptions", Form(format: "text"))).Content.ReadAsStringAsync());
        Assert.Contains("00:00:02,500 --> 00:00:06,000", await (await api.Client.PostAsync("/v1/audio/transcriptions", Form(format: "srt"))).Content.ReadAsStringAsync());
        Assert.StartsWith("WEBVTT", await (await api.Client.PostAsync("/v1/audio/transcriptions", Form(format: "vtt"))).Content.ReadAsStringAsync());
        var verbose = JsonDocument.Parse(await (await api.Client.PostAsync("/v1/audio/transcriptions", Form(format: "verbose_json"))).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("transcribe", Text(verbose, "task"));
        Assert.Equal("german", Text(verbose, "language"));
        Assert.Equal(6.0, verbose.GetProperty("duration").GetDouble());
        Assert.Equal(2, verbose.GetProperty("segments").GetArrayLength());
        Assert.Equal(2.5, verbose.GetProperty("segments")[0].GetProperty("end").GetDouble());

        var models = JsonDocument.Parse(await api.Client.GetStringAsync("/v1/models")).RootElement;
        Assert.Equal("list", Text(models, "object"));
        Assert.Equal(6, api.Repository.Jobs.Count);   // one job per request
    }

    [Fact]
    public async Task TheOpenAiStyleEndpointRefusesABadFormNeatly()
    {
        await using var api = await Harness.StartAsync();
        using var noFile = await api.Client.PostAsync("/v1/audio/transcriptions", Form(file: false));
        Assert.Equal(HttpStatusCode.BadRequest, noFile.StatusCode);
        Assert.Contains("\"message\"", await noFile.Content.ReadAsStringAsync());      // OpenAI clients read error.message
        using var format = await api.Client.PostAsync("/v1/audio/transcriptions", Form(format: "pdf"));
        Assert.Equal(HttpStatusCode.BadRequest, format.StatusCode);
        using var language = await api.Client.PostAsync("/v1/audio/transcriptions", Form(language: "xx-klingon"));
        Assert.Equal(HttpStatusCode.BadRequest, language.StatusCode);
        using var notAForm = await api.Client.PostAsync("/v1/audio/transcriptions", new ByteArrayContent([1, 2, 3]));
        Assert.Equal(HttpStatusCode.BadRequest, notAForm.StatusCode);
        Assert.Empty(api.Repository.Jobs);                                                // nothing was started
        Assert.Empty(Directory.Exists(api.Incoming) ? Directory.GetDirectories(api.Incoming) : []);
    }

    [Fact]
    public async Task AFailedRecordingThroughTheOpenAiStyleEndpointIsAnErrorWithAMessage()
    {
        await using var api = await Harness.StartAsync(configure: h => h.Stages.Fail = "The engine stopped.");
        using var response = await api.Client.PostAsync("/v1/audio/transcriptions", Form());
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal("The engine stopped.", Text(error, "message"));
        Assert.Equal("transcription_failed", Text(error, "code"));
    }

    [Fact]
    public async Task AnUploadOfTwentyMegabytesGoesToDiskIntact()
    {
        var data = new byte[20 * 1024 * 1024];
        new Random(7).NextBytes(data);
        await using var api = await Harness.StartAsync();
        var id = Text(await api.UploadAsync("?name=long.wav", data), "id");
        await api.WaitAsync(id);
        Assert.Equal(data, await File.ReadAllBytesAsync(api.Repository.Jobs[Guid.Parse(id)].SourcePath));
    }

    // ---- a server with no password, and what a client sees of the server ------------------------------------------------------------

    [Fact]
    public async Task WithoutAPasswordTheServerIsOpenAndSaysSo()
    {
        await using var api = await Harness.StartAsync(configure: h => h.CurrentKey = "");
        using var anonymous = new HttpClient { BaseAddress = api.Client.BaseAddress };
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/v1/languages")).StatusCode);
        var health = JsonDocument.Parse(await anonymous.GetStringAsync("/v1/health")).RootElement;
        Assert.False(health.GetProperty("passwordRequired").GetBoolean());
        Assert.Equal("Test server", Text(health, "name"));
        Assert.Equal("Studio", Text(health, "edition"));
        api.CurrentKey = "k7m2-pq9x-w4hd";
        Assert.True(JsonDocument.Parse(await anonymous.GetStringAsync("/v1/health")).RootElement.GetProperty("passwordRequired").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/languages")).StatusCode);
    }

    [Fact]
    public async Task TheServerInfoSaysWhetherItCanTranscribeAndHowBusyItIs()
    {
        string[] missing = [];
        await using var api = await Harness.StartAsync(configure: h => { h.MissingModels = _ => missing; h.Stages.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); });
        var info = JsonDocument.Parse(await api.Client.GetStringAsync("/v1/server")).RootElement;
        Assert.True(info.GetProperty("modelsReady").GetBoolean());
        Assert.False(info.GetProperty("busy").GetBoolean());
        Assert.False(info.GetProperty("encrypted").GetBoolean());
        var first = Text(await api.UploadAsync("?name=a.wav"), "id");
        await api.UploadAsync("?name=b.wav");
        await api.WaitAsync(first, "running");
        info = JsonDocument.Parse(await api.Client.GetStringAsync("/v1/server")).RootElement;
        Assert.True(info.GetProperty("busy").GetBoolean());
        Assert.Equal(1, info.GetProperty("queued").GetInt32());
        api.Stages.Hold!.SetResult();
        missing = ["Whisper large-v3"];
        info = JsonDocument.Parse(await api.Client.GetStringAsync("/v1/server")).RootElement;
        Assert.False(info.GetProperty("modelsReady").GetBoolean());
        Assert.Equal("Whisper large-v3", info.GetProperty("missingModels")[0].GetString());
    }

    // ---- reviewing and editing from another computer ---------------------------------------------------------------------------------

    [Fact]
    public async Task AFinishedTranscriptCanBeReviewedEditedAndFetchedAgain()
    {
        await using var api = await Harness.StartAsync();
        var id = Text(await api.UploadAsync(), "id");
        using (var early = await api.Client.GetAsync($"/v1/transcriptions/{Guid.NewGuid()}/review")) Assert.Equal(HttpStatusCode.NotFound, early.StatusCode);
        await api.WaitAsync(id);
        var review = JsonDocument.Parse(await api.Client.GetStringAsync($"/v1/transcriptions/{id}/review")).RootElement;
        Assert.Equal("de", Text(review, "language"));
        Assert.Equal(2, review.GetProperty("regions").GetArrayLength());
        Assert.Equal("Guten Tag, meine Damen und Herren.", Text(review.GetProperty("regions")[0], "finalText"));
        Assert.Equal("auto: Guten Tag, meine Damen und Herren.", review.GetProperty("automaticTexts")[0].GetString());
        Assert.Equal("raw whisper", Text(review, "rawWhisper"));
        Assert.Equal("raw canary", Text(review, "rawCanary"));

        using var edit = await api.Client.PutAsync($"/v1/transcriptions/{id}/review",
            new StringContent("{\"edits\":[{\"index\":1,\"text\":\"Willkommen zur Sitzung.\"},{\"index\":1,\"text\":\"Willkommen zur Sitzung, alle.\"}]}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var saved = api.Saved!;
        Assert.Equal("Guten Tag, meine Damen und Herren.", saved.Regions[0].FinalText);          // untouched
        Assert.Equal("Willkommen zur Sitzung, alle.", saved.Regions[1].FinalText);
        Assert.Equal("manual", saved.Regions[1].Source);
        Assert.Equal(2, saved.Regions[1].Revisions!.Count);                                       // both edits are in the history

        foreach (var body in new[] { "{\"edits\":[{\"index\":7,\"text\":\"x\"}]}", "{\"edits\":[{\"index\":-1,\"text\":\"x\"}]}", "{\"edits\":null}", "not json" })
        {
            using var bad = await api.Client.PutAsync($"/v1/transcriptions/{id}/review", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }
    }

    [Fact]
    public async Task TheAudioOfAJobIsServedAsTheFileItIs()
    {
        await using var api = await Harness.StartAsync();
        var id = Text(await api.UploadAsync(), "id");
        await api.WaitAsync(id);
        using var playback = await api.Client.GetAsync($"/v1/transcriptions/{id}/audio");
        Assert.Equal("audio/mp4", playback.Content.Headers.ContentType!.MediaType);
        Assert.Equal(new byte[] { 9, 8, 7 }, await playback.Content.ReadAsByteArrayAsync());
        Assert.Equal(3, playback.Content.Headers.ContentLength);
        using var normalized = await api.Client.GetAsync($"/v1/transcriptions/{id}/audio?kind=normalized");
        Assert.Equal("audio/wav", normalized.Content.Headers.ContentType!.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, await normalized.Content.ReadAsByteArrayAsync());
        using var bad = await api.Client.GetAsync($"/v1/transcriptions/{id}/audio?kind=video");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var unknown = await api.Client.GetAsync($"/v1/transcriptions/{Guid.NewGuid()}/audio");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    // ---- the client library against the real thing, encrypted ----------------------------------------------------------------------------

    [Fact]
    public async Task TheClientLibraryDoesEverythingAWindowNeedsOverAnEncryptedConnection()
    {
        await using var api = await Harness.StartAsync();
        var address = new Uri($"https://127.0.0.1:{api.Server.Port}");
        var probe = await RemoteServerClient.ProbeAsync(address, default);
        Assert.Equal(api.Identity.Fingerprint, probe.Fingerprint);
        Assert.True(probe.Health.PasswordRequired);
        using var client = new RemoteServerClient(address, Key, probe.Fingerprint);

        var info = await client.InfoAsync(default);
        Assert.True(info.Encrypted);
        Assert.True(info.ModelsReady);
        var languages = await client.LanguagesAsync(default);
        Assert.Equal("auto", languages[0].Code);
        Assert.Contains(languages, language => language.Code == "hu" && language.SecondEngine);

        var recording = Path.Combine(api.Root, "talk.wav");
        await File.WriteAllBytesAsync(recording, new byte[300_000]);
        var sent = 0L;
        var job = await client.UploadAsync(recording, "de", new Progress<long>(bytes => sent = bytes), default);
        Assert.Equal("talk.wav", job.Name);
        await Task.Delay(100);
        Assert.Equal(300_000, sent);
        job = await client.GetAsync(job.Id, default, waitSeconds: 30);
        Assert.Equal("complete", job.State);
        Assert.True(job.IsFinished);
        Assert.Single(await client.ListAsync(default));

        var review = await client.ReviewAsync(job.Id, default);
        Assert.Equal(2, review.Regions.Count);
        Assert.Equal("raw whisper", review.RawWhisper);
        await client.SaveEditsAsync(job.Id, [new RemoteEdit(0, "Hallo zusammen.")], default);
        Assert.Equal("Hallo zusammen.", api.Saved!.Regions[0].FinalText);

        var srt = System.Text.Encoding.UTF8.GetString(await client.ExportAsync(job.Id, "srt", "strict", default));
        Assert.Contains("-->", srt);
        var downloaded = Path.Combine(api.Root, "downloads", "playback.m4a");
        var received = 0L;
        await client.DownloadAudioAsync(job.Id, "playback", downloaded, new Progress<long>(bytes => received = bytes), default);
        Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(downloaded));
        Assert.False(File.Exists(downloaded + ".part"));

        var missing = await Assert.ThrowsAsync<RemoteException>(() => client.GetAsync(Guid.NewGuid(), default));
        Assert.Equal(404, missing.Status);
        var cancel = await client.CancelAsync(job.Id, default);
        Assert.Equal("complete", cancel.State);                                                    // a finished job stays finished

        using var wrong = new RemoteServerClient(address, "wrong", probe.Fingerprint);
        Assert.True((await Assert.ThrowsAsync<RemoteException>(() => wrong.InfoAsync(default))).IsAuthentication);
    }
}
