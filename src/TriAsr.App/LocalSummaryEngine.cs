using System.IO;
using System.Text.Json;
using TriAsr.Application;
using TriAsr.Domain;
using TriAsr.Engine.Llm;

namespace TriAsr.App;

/// <summary>
/// Summaries made on this computer (Studio, and a server for its clients) with the local language model: the correction model that Studio
/// already downloads (Qwen3-4B), or a larger one placed in Models/Summary. One summary at a time; on the graphics card when there is one, and on the
/// processor when the card has no room for it.
/// </summary>
public sealed class LocalSummaryEngine(IProcessRunner runner, RuntimePaths paths, string dataRoot, TriAsr.Infrastructure.ModelStore? models = null) : ISummaryEngine
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The model a summary is written with: the summary model of the Models page when it is fully downloaded, else the correction model in any of its sizes.</summary>
    public string? Model
    {
        get
        {
            if (models is not null)
                foreach (var entry in TriAsr.Infrastructure.ModelManifest.Entries.Where(entry => entry.Family == "Summary"))
                    if (models.Inspect(entry) is { Installed: true, WrongSize: false }) return models.PathFor(entry);
            if (File.Exists(paths.CorrectionModel)) return paths.CorrectionModel;
            var correction = Path.Combine(paths.ModelRoot, "Models", "Correction");
            return Directory.Exists(correction) ? Directory.GetFiles(correction, "Qwen3-4B-Instruct-2507-*.gguf").OrderByDescending(file => file, StringComparer.OrdinalIgnoreCase).FirstOrDefault() : null;
        }
    }

    public bool IsReady => File.Exists(paths.LlamaServer) && Model is not null;

    public async Task<MeetingSummary> SummarizeAsync(FinalTranscript transcript, IProgress<double>? progress, CancellationToken token)
    {
        if (Model is not { } model || !File.Exists(paths.LlamaServer))
            throw new InvalidOperationException(Loc.Key("No language model for summaries is installed. Download the correction model on the Models page."));
        await _gate.WaitAsync(token);
        try
        {
            var threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 12);
            if (HasGraphicsCard())
                try { return await RunAsync(model, gpu: true, threads, transcript, progress, token); }
                catch (Exception error) when (error is InvalidOperationException or TimeoutException or System.Net.Http.HttpRequestException && !token.IsCancellationRequested) { }   // no room on the card: the processor
            return await RunAsync(model, gpu: false, threads, transcript, progress, token);
        }
        finally { _gate.Release(); }
    }

    private async Task<MeetingSummary> RunAsync(string model, bool gpu, int threads, FinalTranscript transcript, IProgress<double>? progress, CancellationToken token)
    {
        await using var summarizer = new LlamaSummarizer(runner, paths.LlamaServer, model, gpu, threads);
        await summarizer.StartAsync(token);
        return await summarizer.SummarizeAsync(transcript, progress, token);
    }

    /// <summary>Whether the last system check found a graphics card llama.cpp can use (Vulkan).</summary>
    private bool HasGraphicsCard()
    {
        try
        {
            var profile = Path.Combine(dataRoot, "Config", "hardware-profile.json");
            return File.Exists(profile) && JsonSerializer.Deserialize<TriAsr.Hardware.HardwareProfile>(File.ReadAllText(profile))?.VulkanDevices.Count > 0;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>The summaries of the API's recordings, kept as Studio keeps its own (<see cref="SummaryStore"/>).</summary>
public sealed class WorkspaceSummaryKeeper(IJobWorkspace workspace) : ISummaryKeeper
{
    public Task<MeetingSummary?> LoadAsync(Guid id, CancellationToken token) => SummaryStore.LoadAsync(workspace, id, token);
    public Task SaveAsync(Guid id, MeetingSummary summary, CancellationToken token) => SummaryStore.SaveAsync(workspace, id, summary, token);
}

/// <summary>The summary of a project, kept beside its transcript (summary.json in the job's folder).</summary>
public static class SummaryStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task<MeetingSummary?> LoadAsync(IJobWorkspace workspace, Guid id, CancellationToken token = default)
    {
        var path = Path.Combine(workspace.DirectoryFor(id), "summary.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<MeetingSummary>(await File.ReadAllTextAsync(path, token)); }
        catch (JsonException) { return null; }                       // a broken file is as good as none: it is made again on request
    }

    public static async Task SaveAsync(IJobWorkspace workspace, Guid id, MeetingSummary summary, CancellationToken token = default)
    {
        var folder = workspace.DirectoryFor(id);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "summary.json");
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(summary, Json), token);
        File.Move(temporary, path, true);
    }
}
