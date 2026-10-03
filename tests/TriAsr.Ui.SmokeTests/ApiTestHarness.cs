using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using TriAsr.App;
using TriAsr.Application;
using TriAsr.Domain;
using TriAsr.Infrastructure;

namespace TriAsr.Ui.SmokeTests;

/// <summary>What the API tests share: the transcript every fake engine "finds", and a key.</summary>
internal static class ApiTestData
{
    public const string Key = "mbk-test-key-0123456789";
    internal static FinalTranscript Automatic(Guid id, bool native) => Transcript(id, native) with
    {
        Regions = Transcript(id, native).Regions.Select(region => region with { FinalText = "auto: " + region.FinalText }).ToArray()
    };

    internal static FinalTranscript Transcript(Guid id, bool native) => new(id, "de",
    [
        new FinalRegion(0, 2500, "Guten Tag, meine Damen und Herren.", "Guten Tag meine Damen und Herren", "guten Tag, meine Damen und Herren", "agreement", NativeTimestamps: native),
        new FinalRegion(2500, 6000, "Willkommen   zur  Sitzung .", "Willkommen zur Sitzung", "willkommen zur Sitzung", "uncertain", NativeTimestamps: native)
    ]);

    internal static string Text(JsonElement element, string name) => element.GetProperty(name).GetString()!;
}

internal sealed class MemoryRepository : IJobRepository
{
    public Dictionary<Guid, TranscriptionJob> Jobs { get; } = [];
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SaveAsync(TranscriptionJob job, CancellationToken cancellationToken = default) { lock (Jobs) Jobs[job.Id] = job; return Task.CompletedTask; }
    public Task<IReadOnlyList<TranscriptionJob>> ListAsync(CancellationToken cancellationToken = default) { lock (Jobs) return Task.FromResult<IReadOnlyList<TranscriptionJob>>(Jobs.Values.ToArray()); }
    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) { lock (Jobs) Jobs.Remove(id); return Task.CompletedTask; }
}

internal sealed class FakeWorkspace(string root) : IJobWorkspace
{
    public string DirectoryFor(Guid jobId) => Path.Combine(root, "Jobs", jobId.ToString("N"));
    public Task CreateAsync(TranscriptionJob job, CancellationToken cancellationToken) { Directory.CreateDirectory(DirectoryFor(job.Id)); return Task.CompletedTask; }
}

internal sealed class FakeAudio : IAudioNormalizer
{
    public Task<AudioInfo> NormalizeAsync(string source, string destination, CancellationToken cancellationToken) => Task.FromResult(new AudioInfo(16000, 1, 16, 16000));
}

internal sealed class FakeStages : ITranscriptionStages
{
    public TaskCompletionSource? Hold;
    public string? Fail;
    public List<Guid> Started { get; } = [];
    public async Task ExecuteAsync(TranscriptionJob job, JobState stage, CancellationToken token)
    {
        if (stage == JobState.RunningWhisper)
        {
            lock (Started) Started.Add(job.Id);
            if (Hold is { } hold) await hold.Task.WaitAsync(token);
            if (Fail is { } message) throw new InvalidOperationException(message.Replace("{source}", job.SourcePath));
        }
    }
    public Task<FinalTranscript> LoadFinalAsync(Guid id, CancellationToken token = default) => throw new NotSupportedException();
    public Task SaveManualAsync(FinalTranscript transcript, CancellationToken token = default) => throw new NotSupportedException();
}

/// <summary>Stands in for the speech program that reads one phrase of live dictation: it records what it was given and answers what the test says.</summary>
internal sealed class FakeLive : ILiveRecognizer
{
    public bool Ready { get; set; } = true;
    public string Answer { get; set; } = "Hello there.";
    public string AnswerLanguage { get; set; } = "en";
    public Func<byte[], string, Exception?>? Fail { get; set; }
    public TimeSpan Delay { get; set; }
    public List<(byte[] Wav, string Language, string? Recent)> Calls { get; } = [];

    public async Task<LivePhrase> RecognizeAsync(byte[] wav, string language, string? recent, CancellationToken token)
    {
        lock (Calls) Calls.Add((wav, language, recent));
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, token);
        if (Fail?.Invoke(wav, language) is { } error) throw error;
        return new(Answer, AnswerLanguage);
    }
}

/// <summary>Stands in for the program that downloads the sound of a link: it records what it was asked, can be held, can fail, and writes a small file.</summary>
internal sealed class FakeLinks : ILinkFetcher
{
    public bool PagesReady { get; set; }
    public TaskCompletionSource? Hold { get; set; }
    public Func<Uri, Exception?>? Fail { get; set; }
    public double[] Progress { get; set; } = [];
    public List<(Uri Link, string Folder, LinkFetchOptions Options)> Calls { get; } = [];
    public CancellationToken LastToken { get; private set; }

    /// <summary>The real rule for addresses that are numbers or <c>localhost</c>; names are not looked up (the test machine may have no DNS), so any other name passes.</summary>
    public Task CheckAsync(Uri link, bool allowPrivateNetwork, CancellationToken token) =>
        link.HostNameType == UriHostNameType.Dns && !link.IdnHost.Contains("localhost", StringComparison.OrdinalIgnoreCase) ? Task.CompletedTask : LinkPolicy.EnsureAllowedAsync(link, allowPrivateNetwork, token);

    public async Task<FetchedLink> FetchAsync(Uri link, string folder, LinkFetchOptions options, IProgress<double>? percent, CancellationToken token)
    {
        lock (Calls) Calls.Add((link, folder, options));
        LastToken = token;
        foreach (var value in Progress) percent?.Report(value);
        if (Hold is { } hold) await hold.Task.WaitAsync(token);
        if (Fail?.Invoke(link) is { } error) throw error;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Fetched Talk.mp3");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4], token);
        return new FetchedLink(path, "Fetched Talk");
    }
}

internal sealed class Harness : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
    public MemoryRepository Repository { get; } = new();
    public FakeStages Stages { get; } = new();
    public Func<string, string[]> MissingModels { get; set; } = _ => [];
    public bool Native { get; set; } = true;
    public string CurrentKey { get; set; } = ApiTestData.Key;
    public string Name { get; set; } = "Test server";
    /// <summary>Fetches the sound of links for <c>POST /v1/links</c>; null makes the server one that does not fetch links.</summary>
    public FakeLinks? Links { get; set; }
    /// <summary>Reads the phrases of live dictation for <c>POST /v1/live</c>; null makes the server one that does not read dictation.</summary>
    public FakeLive? Live { get; set; }
    /// <summary>Keeps the notes for <c>/v1/notes</c>; null makes the server one that keeps no notes.</summary>
    public INoteStore? Notes { get; set; }
    /// <summary>Deletes a finished recording with its files, the real thing over the harness's repository and folders.</summary>
    public ProjectRemoval Removal { get; private set; } = null!;
    public FinalTranscript? Saved { get; private set; }
    public ServerIdentity Identity { get; private set; } = null!;
    public ApiService Service { get; private set; } = null!;
    public LocalHttpServer Server { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public string Incoming => Path.Combine(Root, "Api", "Incoming");
    public List<bool> Busy { get; } = [];

    public static async Task<Harness> StartAsync(Action<Harness>? configure = null, Func<Harness, Task>? before = null)
    {
        var harness = new Harness();
        configure?.Invoke(harness);
        Directory.CreateDirectory(harness.Root);
        var queue = new AudioJobQueue(harness.Repository, new FakeWorkspace(harness.Root), new FakeAudio());
        var pipeline = new TranscriptionPipeline(harness.Repository, harness.Stages);
        harness.Removal = new ProjectRemoval(harness.Repository, new FakeWorkspace(harness.Root), new StoragePaths(harness.Root));
        harness.Service = new ApiService(new ApiServiceDependencies(queue, pipeline, harness.Repository,
            (id, _) => Task.FromResult(ApiTestData.Transcript(id, harness.Native)), harness.Incoming, Path.Combine(harness.Root, "Api", "Exports"), "0.0.0-test",
            () => harness.CurrentKey, language => harness.MissingModels(language), () => true, busy => { lock (harness.Busy) harness.Busy.Add(busy); },
            () => harness.Name, "Studio", (id, _) => Task.FromResult(new ReviewBundle(ApiTestData.Transcript(id, harness.Native), ApiTestData.Automatic(id, harness.Native), "raw whisper", "raw canary", null)),
            (transcript, _) => { harness.Saved = transcript; return Task.CompletedTask; }, (id, kind) => harness.AudioFile(kind), harness.Links, (job, token) => harness.Removal.DeleteAsync(job, token), harness.Live, () => harness.Live?.Ready ?? true, harness.Notes));
        if (before is not null) await before(harness);
        await harness.Service.StartAsync();
        harness.Identity = ServerIdentity.LoadOrCreate(Path.Combine(harness.Root, "Identity"));
        harness.Server = new LocalHttpServer(new HttpServerOptions(System.Net.IPAddress.Loopback, 0, Certificate: harness.Identity.Certificate), harness.Service.HandleAsync);
        harness.Server.Start();
        harness.Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{harness.Server.Port}"), Timeout = TimeSpan.FromSeconds(60) };
        harness.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiTestData.Key);
        return harness;
    }

    public string? AudioFile(string kind)
    {
        var path = Path.Combine(Root, kind == "normalized" ? "normalized.wav" : "playback.m4a");
        if (!File.Exists(path)) File.WriteAllBytes(path, kind == "normalized" ? [1, 2, 3, 4, 5, 6] : [9, 8, 7]);
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Server.DisposeAsync();
        await Service.DisposeAsync();
        Identity.Dispose();
        TestCleanup.Delete(Root);
    }

    public async Task<JsonElement> UploadAsync(string query = "?language=de&name=meeting.wav", byte[]? data = null)
    {
        using var response = await Client.PostAsync("/v1/transcriptions" + query, new ByteArrayContent(data ?? new byte[2048]));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.StartsWith("/v1/transcriptions/", response.Headers.Location!.OriginalString);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    public async Task<JsonElement> WaitAsync(string id, string until = "complete")
    {
        for (var i = 0; i < 200; i++)
        {
            var status = JsonDocument.Parse(await Client.GetStringAsync($"/v1/transcriptions/{id}")).RootElement.Clone();
            if (status.GetProperty("state").GetString() == until) return status;
            await Task.Delay(50);
        }
        throw new TimeoutException($"The job never reached {until}.");
    }
}

