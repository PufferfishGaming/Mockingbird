using System.Net;
using System.Net.Http;
using System.Text.Json;
using TriAsr.Application;
using TriAsr.Infrastructure;

namespace TriAsr.Ui.SmokeTests;

/// <summary><c>POST /v1/live</c> (ADR: live dictation): a Client sends one phrase as a WAV file and gets the words back; the server runs only the speech program for it.</summary>
public sealed class LiveApiTests
{
    private static byte[] Wav(int seconds = 2) => PhraseText.Wav(new byte[seconds * 32_000]);

    private static async Task<HttpResponseMessage> PostAsync(Harness api, byte[] wav, string query = "?language=de") =>
        await api.Client.PostAsync("/v1/live" + query, new ByteArrayContent(wav) { Headers = { { "Content-Type", "audio/wav" } } });

    private static string ErrorCode(string body) => JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetString()!;

    [Fact]
    public async Task APhraseGoesInAsAWavFileAndTheWordsComeBack()
    {
        var live = new FakeLive { Answer = "Guten Tag, meine Damen und Herren." };
        await using var api = await Harness.StartAsync(h => h.Live = live);
        var wav = Wav();
        using var response = await PostAsync(api, wav);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Guten Tag, meine Damen und Herren.", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("text").GetString());
        var call = Assert.Single(live.Calls);
        Assert.Equal(wav, call.Wav);
        Assert.Equal("de", call.Language);
        using var automatic = await PostAsync(api, wav, query: "");                          // the language is optional
        Assert.Equal("auto", live.Calls[^1].Language);
        Assert.Empty(api.Repository.Jobs);                                                    // dictation is not a project: nothing is stored
    }

    private static async Task<string> TextOfAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("text").GetString()!;

    [Fact]
    public async Task TheWordsComeBackWithoutTheProgramsMarkersAndWithoutTheWordsInventedForSoundThatHoldsNoSpeech()
    {
        var live = new FakeLive { Answer = "[BLANK_AUDIO] Hello   there. (music)" };
        await using var api = await Harness.StartAsync(h => h.Live = live);
        using var tidy = await PostAsync(api, Wav(), "?language=en");
        Assert.Equal("Hello there.", await TextOfAsync(tidy));                                // markers and blanks are gone

        live.Answer = "Thank you.";
        using var unknown = await PostAsync(api, Wav(), "?language=en");
        Assert.Equal("Thank you.", await TextOfAsync(unknown));                                // without a hint how much was speech the words are left alone
        using var brief = await PostAsync(api, Wav(), "?language=en&speech=600");
        Assert.Equal("", await TextOfAsync(brief));                                            // 0.6 s of sound: the words Whisper invents for a cough
        using var spoken = await PostAsync(api, Wav(), "?language=en&speech=3000");
        Assert.Equal("Thank you.", await TextOfAsync(spoken));                                 // the same words in a long phrase are a sentence
        using var garbage = await PostAsync(api, Wav(), "?language=en&speech=lots");
        Assert.Equal("Thank you.", await TextOfAsync(garbage));

        live.Answer = "[BLANK_AUDIO]";
        using var nothing = await PostAsync(api, Wav(), "?language=en&speech=900");
        Assert.Equal("", await TextOfAsync(nothing));
    }
    [Fact]
    public async Task ItNeedsThePasswordLikeEverythingElse()
    {
        var live = new FakeLive();
        await using var api = await Harness.StartAsync(h => h.Live = live);
        using var anonymous = new HttpClient { BaseAddress = api.Client.BaseAddress };
        using var refused = await anonymous.PostAsync("/v1/live", new ByteArrayContent(Wav()));
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Empty(live.Calls);
        using var wrongMethod = await api.Client.GetAsync("/v1/live");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
    }

    [Fact]
    public async Task ARequestThatIsNotAPhraseIsRefusedBeforeAnythingIsRead()
    {
        var live = new FakeLive();
        await using var api = await Harness.StartAsync(h => h.Live = live);
        using var tiny = await PostAsync(api, [1, 2, 3]);
        Assert.Equal(HttpStatusCode.BadRequest, tiny.StatusCode);
        Assert.Equal("empty_upload", ErrorCode(await tiny.Content.ReadAsStringAsync()));
        using var language = await PostAsync(api, Wav(), "?language=klingon");
        Assert.Equal("unsupported_language", ErrorCode(await language.Content.ReadAsStringAsync()));
        // Far more than a phrase: the server refuses it before reading it, so the client sees the 413 answer or the connection being closed while it is still sending.
        try
        {
            using var huge = await PostAsync(api, new byte[3 * 1024 * 1024]);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);
        }
        catch (HttpRequestException) { }
        Assert.Empty(live.Calls);
    }

    [Fact]
    public async Task WhatTheSpeechProgramCannotDoBecomesAnAnswerThatSaysWhy()
    {
        var live = new FakeLive();
        await using var api = await Harness.StartAsync(h => h.Live = live);
        live.Fail = (_, _) => new LiveException(LiveMessages.NoModel);
        using var noModel = await PostAsync(api, Wav());
        Assert.Equal(HttpStatusCode.Conflict, noModel.StatusCode);
        Assert.Equal("models_missing", ErrorCode(await noModel.Content.ReadAsStringAsync()));
        live.Fail = (_, _) => new LiveException(LiveMessages.Failed);
        using var failed = await PostAsync(api, Wav());
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Equal("live_failed", ErrorCode(await failed.Content.ReadAsStringAsync()));
        live.Fail = (_, _) => new LiveException(LiveMessages.TooLong);
        using var tooLong = await PostAsync(api, Wav());
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLong.StatusCode);
    }

    [Fact]
    public async Task AServerWithoutTheProgramSaysItDoesNotReadDictationAndTheInfoTellsClientsInAdvance()
    {
        await using var plain = await Harness.StartAsync();
        using var none = await PostAsync(plain, Wav());
        Assert.Equal(HttpStatusCode.NotImplemented, none.StatusCode);
        Assert.Equal("live_unavailable", ErrorCode(await none.Content.ReadAsStringAsync()));
        Assert.False(JsonDocument.Parse(await plain.Client.GetStringAsync("/v1/server")).RootElement.GetProperty("liveEnabled").GetBoolean());

        var live = new FakeLive();
        await using var api = await Harness.StartAsync(h => h.Live = live);
        Assert.True(JsonDocument.Parse(await api.Client.GetStringAsync("/v1/server")).RootElement.GetProperty("liveEnabled").GetBoolean());
        live.Ready = false;                                                                    // no speech model installed yet
        Assert.False(JsonDocument.Parse(await api.Client.GetStringAsync("/v1/server")).RootElement.GetProperty("liveEnabled").GetBoolean());
    }

    [Fact]
    public async Task PhrasesAreReadWhileARecordingIsBeingTranscribedAndDoNotJoinItsQueue()
    {
        var live = new FakeLive();
        await using var api = await Harness.StartAsync(h => { h.Live = live; h.Stages.Hold = new TaskCompletionSource(); });
        var id = ApiTestData.Text(await api.UploadAsync(), "id");
        await api.WaitAsync(id, "running");
        using var response = await PostAsync(api, Wav());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("running", ApiTestData.Text(JsonDocument.Parse(await api.Client.GetStringAsync($"/v1/transcriptions/{id}")).RootElement, "state"));
        Assert.Single(api.Repository.Jobs);
        api.Stages.Hold!.SetResult();
        await api.WaitAsync(id);
    }

    [Fact]
    public async Task TheClientLibraryAndTheRemoteRecognizerCarryAPhraseAndItsErrors()
    {
        var live = new FakeLive { Answer = "Remote words." };
        await using var api = await Harness.StartAsync(h => h.Live = live);
        using var client = new RemoteServerClient(api.Client.BaseAddress!, ApiTestData.Key, null);
        Assert.Equal("Remote words.", await client.LiveAsync(Wav(), "en", default));
        Assert.True((await client.InfoAsync(default)).LiveEnabled);

        var recognizer = new RemoteLiveRecognizer(client);
        Assert.Equal("Remote words.", await recognizer.RecognizeAsync(Wav(), "hu", default));
        Assert.Equal("hu", live.Calls[^1].Language);

        live.Fail = (_, _) => new LiveException(LiveMessages.NoModel);
        var missing = await Assert.ThrowsAsync<LiveException>(() => recognizer.RecognizeAsync(Wav(), "de", default));
        Assert.Equal(LiveMessages.NoModel, missing.Message);                                  // the server's own sentence, so that the window can translate it
        live.Fail = null;

        using var wrong = new RemoteServerClient(api.Client.BaseAddress!, "wrong", null);
        await Assert.ThrowsAsync<LiveException>(() => new RemoteLiveRecognizer(wrong).RecognizeAsync(Wav(), "de", default));
    }

    [Fact]
    public async Task AnOlderServerThatDoesNotKnowTheCallIsReportedNotThrownAtTheCaller()
    {
        await using var api = await Harness.StartAsync();
        using var client = new RemoteServerClient(api.Client.BaseAddress!, ApiTestData.Key, null);
        var error = await Assert.ThrowsAsync<LiveException>(() => new RemoteLiveRecognizer(client).RecognizeAsync(Wav(), "de", default));
        Assert.Contains("does not read live dictation", error.Message);
    }
}
