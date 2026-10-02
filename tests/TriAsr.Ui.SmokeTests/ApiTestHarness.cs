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

internal sealed class Harness : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
    public MemoryRepository Repository { get; } = new();
    public FakeStages Stages { get; } = new();
    public Func<string, string[]> MissingModels { get; set; } = _ => [];
    public bool Native { get; set; } = true;
    public string CurrentKey { get; set; } = ApiTestData.Key;
    public string Name { get; set; } = "Test server";
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
        harness.Service = new ApiService(new ApiServiceDependencies(queue, pipeline, harness.Repository,
            (id, _) => Task.FromResult(ApiTestData.Transcript(id, harness.Native)), harness.Incoming, Path.Combine(harness.Root, "Api", "Exports"), "0.0.0-test",
            () => harness.CurrentKey, language => harness.MissingModels(language), () => true, busy => { lock (harness.Busy) harness.Busy.Add(busy); },
            () => harness.Name, "Studio", (id, _) => Task.FromResult(new ReviewBundle(ApiTestData.Transcript(id, harness.Native), ApiTestData.Automatic(id, harness.Native), "raw whisper", "raw canary", null)),
            (transcript, _) => { harness.Saved = transcript; return Task.CompletedTask; }, (id, kind) => harness.AudioFile(kind)));
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

