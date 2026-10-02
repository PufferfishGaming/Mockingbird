using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using TriAsr.Application;
using TriAsr.Domain;
using TriAsr.Export;
using TriAsr.Benchmark;

namespace TriAsr.Ui.SmokeTests;

public sealed class RecoveryAndExportTests
{
    [Fact]
    public async Task FailedCanaryRunRetainsWhisperCheckpointAndResumes()
    {
        var repository = new Repository(); var stages = new Stages();
        var pipeline = new TranscriptionPipeline(repository, stages);
        var job = new TranscriptionJob(Guid.NewGuid(), "fixture", "de", JobState.Queued, DateTimeOffset.UtcNow);
        var failed = await pipeline.RunAsync(job);
        Assert.Equal(JobState.Failed, failed.State); Assert.Equal("RunningWhisper", failed.Checkpoint);
        stages.Fail = false;
        var complete = await pipeline.RunAsync(failed);
        Assert.Equal(JobState.Complete, complete.State); Assert.Equal(1, stages.WhisperExecutions);
        Assert.Contains(repository.Events, item => item.State == JobState.RunningCanary);
    }
    [Fact]
    public async Task ExportsKeepAccentsProvenanceAndLongDurationTiming()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var region = new FinalRegion(90000000, 90001234, "Árvíztűrő \"tükör\" & <szó>", "raw", "alternative", "manual", "canary", .9,
                [new(DateTimeOffset.UtcNow, "old", "new")]);
            var transcript = new FinalTranscript(Guid.NewGuid(), "hu", [region]);
            foreach (var extension in new[] { "txt", "md", "srt", "vtt", "json", "csv", "docx" })
                await TranscriptExporter.SaveAsync(transcript, Path.Combine(directory, "transcript." + extension));
            Assert.Contains("25:00:00,000 --> 25:00:01,234", await File.ReadAllTextAsync(Path.Combine(directory, "transcript.srt")));
            Assert.StartsWith("WEBVTT\n\n25:00:00.000", await File.ReadAllTextAsync(Path.Combine(directory, "transcript.vtt")));
            Assert.Equal(region, JsonSerializer.Deserialize<FinalTranscript>(await File.ReadAllTextAsync(Path.Combine(directory, "transcript.json")))!.Regions[0] with { Revisions = region.Revisions });
            using var zip = ZipFile.OpenRead(Path.Combine(directory, "transcript.docx"));
            using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
            var document = XDocument.Parse(await reader.ReadToEndAsync());
            Assert.Contains(document.Descendants().Where(item => item.Name.LocalName == "t"), item => item.Value == region.FinalText);
            Assert.Contains("\"\"tükör\"\"", await File.ReadAllTextAsync(Path.Combine(directory, "transcript.csv")));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void ManualEditsPreserveOriginalCandidatesAndHistory()
    {
        var original = new FinalRegion(0, 1000, "first", "Whisper", "Canary", "llm-arbitrated", "whisper", .8);
        var region = new App.ReviewRegion(original) { Text = "edited" }; region.AcceptSaved(); region.Text = "again";
        var current = region.Snapshot();
        Assert.Equal("Whisper", current.WhisperText); Assert.Equal("Canary", current.CanaryText);
        Assert.Equal("whisper", current.LlmChoice); Assert.Equal(2, current.Revisions!.Count);
        Assert.Equal("first", current.Revisions[0].PreviousText); Assert.Equal("edited", current.Revisions[1].PreviousText);
    }
    [Fact]
    public async Task WarmupIsExcludedFromBenchmarkRanking()
    {
        var row = await BenchmarkSession.MeasureAsync("fixture", "model", "cpu", 4, "single", 10,
            (index, _) => Task.FromResult(new Measurement(index == -1 ? 999 : index + 1)), CancellationToken.None);
        Assert.Equal(3, row.Runs.Count); Assert.Equal(2, row.MedianSeconds); Assert.Equal(.2, row.RealTimeFactor);
    }
    private sealed class Repository : IJobRepository
    {
        public List<TranscriptionJob> Events { get; } = [];
        public Task InitializeAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task SaveAsync(TranscriptionJob job, CancellationToken token = default) { Events.Add(job); return Task.CompletedTask; }
        public Task<IReadOnlyList<TranscriptionJob>> ListAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<TranscriptionJob>>(Events);
        public Task DeleteAsync(Guid id, CancellationToken token = default) => Task.CompletedTask;
    }
    private sealed class Stages : ITranscriptionStages
    {
        private readonly HashSet<JobState> _completed = [];
        public bool Fail { get; set; } = true;
        public int WhisperExecutions { get; private set; }
        public Task ExecuteAsync(TranscriptionJob job, JobState stage, CancellationToken token)
        {
            if (_completed.Contains(stage)) return Task.CompletedTask;
            if (stage == JobState.RunningCanary && Fail) throw new IOException("Fixture native worker failed.");
            if (stage == JobState.RunningWhisper) WhisperExecutions++;
            _completed.Add(stage); return Task.CompletedTask;
        }
        public Task<FinalTranscript> LoadFinalAsync(Guid id, CancellationToken token = default) => throw new NotSupportedException();
        public Task SaveManualAsync(FinalTranscript transcript, CancellationToken token = default) => Task.CompletedTask;
    }
}
