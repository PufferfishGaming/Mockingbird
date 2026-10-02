using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TriAsr.Application;
using TriAsr.Audio;
using TriAsr.Domain;
using TriAsr.Engine.Canary;
using TriAsr.Engine.Llm;
using TriAsr.Engine.Whisper;
using TriAsr.Fusion;
using TriAsr.Hardware;
using TriAsr.Benchmark;
using TriAsr.Infrastructure;

namespace TriAsr.App;

public sealed class LocalTranscriptionStages(IJobWorkspace workspace, IAudioNormalizer audio, IProcessRunner runner,
    RuntimePaths paths, IStoragePaths storage, ModelStore models, IRecordRepository records, ResourceGovernor governor, SettingsStore settings) : ITranscriptionStages, IProgressReportingStages
{
    public event EventHandler<StageProgress>? StageProgressChanged;
    public event EventHandler<(string Title, string Message)>? IssueOccurred;
    private void Issue(string title, string message) => IssueOccurred?.Invoke(this, (title, message));
    private void Report(Guid id, JobState stage, double fraction) => StageProgressChanged?.Invoke(this, new(id, stage, fraction));
    private void CanaryProgress(Guid id, string line)
    {
        const string prefix = "TRIASR_PROGRESS ";
        if (line.StartsWith(prefix, StringComparison.Ordinal) && double.TryParse(line[prefix.Length..], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var fraction)) Report(id, JobState.RunningCanary, fraction);
    }
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record Correction(Disagreement Disagreement, ArbitrationDecision Decision, string? Error);
    private string FileFor(Guid id, string name) => Path.Combine(workspace.DirectoryFor(id), name);
    private async Task<T> Read<T>(Guid id, string name, CancellationToken token)
    {
        var json = await File.ReadAllTextAsync(FileFor(id, name), token);
        var value = JsonSerializer.Deserialize<T>(json) ?? throw new InvalidDataException($"Invalid checkpoint {name}.");
        await records.SaveAsync(new(Kind(name), id.ToString("N") + "/" + name, json, id), token);
        return value;
    }
    private static string Kind(string name) => name switch
    {
        "source.json" => "sources", "whisper.json" or "canary.json" => "engine_runs", "comparison.json" => "alignment",
        "corrections.json" => "corrections", "final.json" => "final", "edited.json" => "edited",
        _ => name.StartsWith("Revisions/") ? "manual_revisions" : "checkpoints"
    };
    private async Task Write<T>(Guid id, string name, T value, CancellationToken token)
    {
        var target = FileFor(id, name); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var serialized = JsonSerializer.Serialize(value, Json);
            await File.WriteAllTextAsync(temporary, serialized, token); File.Move(temporary, target, true);
            await records.SaveAsync(new(Kind(name), id.ToString("N") + "/" + name, serialized, id), token);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task ExecuteAsync(TranscriptionJob job, JobState stage, CancellationToken token)
    {
        var directory = workspace.DirectoryFor(job.Id);
        var normalized = FileFor(job.Id, "normalized.wav");
        if (stage == JobState.Preprocessing)
        {
            using var source = JsonDocument.Parse(await File.ReadAllTextAsync(FileFor(job.Id, "source.json"), token));
            await records.SaveAsync(new("sources", job.Id.ToString("N"), source.RootElement.GetRawText(), job.Id), token);
            await using var stream = File.OpenRead(job.SourcePath);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
            if (source.RootElement.TryGetProperty("Sha256", out var hash) && hash.GetString() != digest)
                throw new InvalidDataException("The source file changed. Create a new job to keep its evidence consistent.");
            var saved = File.Exists(FileFor(job.Id, "configuration.json")) ? await Read<JobConfiguration>(job.Id, "configuration.json", token) : null;
            var preferences = saved is null ? await settings.LoadAsync() : null;
            var config = JobConfiguration.Bind(await GetConfiguration(token), saved, preferences?.SkipNonSpeech ?? false, preferences?.UseCorrectionModel ?? false);
            if (saved is null) await Write(job.Id, "configuration.json", config, token);
            await AudioPreparation.NormalizeAsync(audio, job.SourcePath, normalized, FileFor(job.Id, "playback.m4a"), token);
            await PlanChunksAsync(job.Id, normalized, config.WhisperThreads, token);
            return;
        }
        var configuration = await Read<JobConfiguration>(job.Id, "configuration.json", token);
        var seconds = WaveAudio.Inspect(normalized).DurationSeconds;
        var whisper = new WhisperEngine(runner, paths.WhisperFor(configuration.WhisperBackend), paths.WhisperModel, governor.Clamp(configuration.WhisperThreads));
        switch (stage)
        {
            case JobState.DetectingLanguage:
                if (File.Exists(FileFor(job.Id, "language.json"))) { await Read<WhisperEngine.LanguageDetection>(job.Id, "language.json", token); return; }
                WhisperEngine.LanguageDetection detected;
                // Samples are taken on speech only for jobs that skip silence; every other job samples the start, middle and end as before.
                var plan = configuration.SkipNonSpeech ? await TryReadPlanAsync(job.Id, token) : null;
                var sampleStarts = plan is null ? null : LanguageSamples.From(plan, seconds);
                try
                {
                    detected = job.Language == "auto"
                        ? await whisper.DetectLanguageAsync(normalized, seconds, FileFor(job.Id, "Language"), configuration.WhisperBackend, paths.Ffmpeg, token, sampleStarts)
                        : new WhisperEngine.LanguageDetection(job.Language, 1, [job.Language]);
                }
                catch (Exception error) when (error is not OperationCanceledException && configuration.WhisperBackend != "cpu")
                {
                    await Write(job.Id, "Language/fallback.json", new { Error = error.Message, Requested = configuration.WhisperBackend, Retry = "cpu" }, token);
                    Issue("GPU language detection failed", "Retrying on CPU. " + error.Message);
                    detected = await new WhisperEngine(runner, paths.Whisper, paths.WhisperModel, governor.Clamp(configuration.WhisperThreads)).DetectLanguageAsync(normalized, seconds, FileFor(job.Id, "LanguageCpu"), "cpu", paths.Ffmpeg, token, sampleStarts);
                }
                if (!LanguageCatalog.Supports(detected.Language)) throw new InvalidDataException($"Detected unsupported language '{detected.Language}'. Choose a language listed in Languages.");
                if (detected.Confidence < .65) throw new InvalidDataException("Language detection is uncertain. Create a job with an explicit speech language.");
                await Write(job.Id, "language.json", detected, token); break;
            case JobState.RunningWhisper:
                if (File.Exists(FileFor(job.Id, "whisper.json"))) { await Read<EngineTranscript>(job.Id, "whisper.json", token); return; }
                var language = (await Read<WhisperEngine.LanguageDetection>(job.Id, "language.json", token)).Language;
                EngineTranscript first;
                var vadModel = await SkipModelAsync(job.Id, configuration, "Whisper", token);
                try { first = await whisper.TranscribeAsync(normalized, seconds, language, FileFor(job.Id, "Whisper"), configuration.WhisperBackend, token, value => Report(job.Id, stage, value), vadModel); }
                catch (Exception error) when (error is not OperationCanceledException && configuration.WhisperBackend != "cpu")
                {
                    await Write(job.Id, "Whisper/fallback.json", new { Error = error.Message, Requested = configuration.WhisperBackend, Retry = "cpu" }, token);
                    Issue("Whisper GPU attempt failed", "Retrying on CPU. " + error.Message);
                    first = await new WhisperEngine(runner, paths.Whisper, paths.WhisperModel, governor.Clamp(configuration.WhisperThreads)).TranscribeAsync(normalized, seconds, language, FileFor(job.Id, "WhisperCpu"), "cpu", token, value => Report(job.Id, stage, value), vadModel);
                }
                first = await RemoveLoopsAsync(job.Id, first, configuration.SkipNonSpeech, token);
                await Write(job.Id, "whisper.json", first, token); break;
            case JobState.RunningCanary:
                if (File.Exists(FileFor(job.Id, "canary.json"))) { await Read<CanaryNative.Result>(job.Id, "canary.json", token); return; }
                var speechLanguage = (await Read<WhisperEngine.LanguageDetection>(job.Id, "language.json", token)).Language;
                if (!LanguageCatalog.CanaryCodes.Contains(speechLanguage))
                {
                    await Write(job.Id, "Canary/skipped-language.json", new { Language = speechLanguage, Reason = "Outside Canary's 25-language coverage", ReviewRequired = true }, token);
                    break;
                }
                var raw = FileFor(job.Id, "Canary/raw.json"); Directory.CreateDirectory(Path.GetDirectoryName(raw)!);
                if (!File.Exists(paths.CanaryModel))
                    throw new FileNotFoundException($"Selected Canary model {Path.GetFileName(paths.CanaryModel)} is not downloaded. Open Models and download it, or choose the Balanced preset to use the installed Q8 model.", paths.CanaryModel);
                // A job that skips silence and music gives Canary the speech windows of its chunk plan (cut in real pauses, finished windows
                // are kept for a restart). Every other job reads the whole file exactly as before, so its result does not change.
                IReadOnlyList<CanaryWindow>? windows = null;
                if (configuration.SkipNonSpeech)
                {
                    var chunkPlan = await TryReadPlanAsync(job.Id, token);
                    windows = chunkPlan is null ? null : CanaryWindow.SpeechWindows(chunkPlan, (long)Math.Round(seconds * 1000));
                    if (windows is null) await SkipUnavailableAsync(job.Id, "Canary", token);
                }
                var request = new CanaryRequest(paths.CanaryFor(configuration.CanaryBackend), paths.CanaryModel, normalized, speechLanguage,
                    configuration.CanaryBackend, governor.Clamp(configuration.CanaryThreads), raw, windows, windows is null ? null : FileFor(job.Id, "Canary/windows"));
                await Write(job.Id, "Canary/request.json", request, token);
                var result = await runner.RunAsync(new(paths.CanaryWorker, ["--canary", FileFor(job.Id, "Canary/request.json")],
                    Path.GetDirectoryName(paths.CanaryWorker)!, TimeSpan.FromHours(12), line => CanaryProgress(job.Id, line)), token);
                await File.WriteAllTextAsync(FileFor(job.Id, "Canary/runtime.stderr.txt"), result.StandardError, token);
                if (result.ExitCode != 0 && configuration.CanaryBackend != "cpu")
                {
                    await Write(job.Id, "Canary/fallback.json", new { Error = result.StandardError, Requested = configuration.CanaryBackend, Retry = "cpu" }, token);
                    Issue("Canary GPU attempt failed", "Retrying on CPU. The attempt output is saved in the job folder.");
                    request = request with { RuntimeDirectory = paths.CanaryRuntime, Backend = "cpu", Output = FileFor(job.Id, "CanaryCpu/raw.json") };
                    Directory.CreateDirectory(FileFor(job.Id, "CanaryCpu"));
                    await Write(job.Id, "CanaryCpu/request.json", request, token);
                    result = await runner.RunAsync(new(paths.CanaryWorker, ["--canary", FileFor(job.Id, "CanaryCpu/request.json")],
                        Path.GetDirectoryName(paths.CanaryWorker)!, TimeSpan.FromHours(12), line => CanaryProgress(job.Id, line)), token);
                    await File.WriteAllTextAsync(FileFor(job.Id, "CanaryCpu/runtime.stderr.txt"), result.StandardError, token);
                }
                if (result.ExitCode != 0) throw new InvalidOperationException("Canary failed. Whisper evidence is saved; resume after fixing the runtime. " + result.StandardError[^Math.Min(500, result.StandardError.Length)..]);
                var second = await Read<CanaryNative.Result>(job.Id, request.Backend == "cpu" && configuration.CanaryBackend != "cpu" ? "CanaryCpu/raw.json" : "Canary/raw.json", token);
                if (second.Transcript.ActualBackend != request.Backend) throw new InvalidDataException("Canary backend mismatch.");
                await Write(job.Id, "canary.json", second, token); break;
            case JobState.Aligning:
                if (File.Exists(FileFor(job.Id, "comparison.json"))) { await Read<ComparisonResult>(job.Id, "comparison.json", token); return; }
                if (!File.Exists(FileFor(job.Id, "whisper.json")))
                {
                    if (!File.Exists(FileFor(job.Id, "canary.json"))) throw new InvalidOperationException("Both speech engines failed. Their failure evidence is retained in the job folder.");
                    var survivingCanary = (await Read<CanaryNative.Result>(job.Id, "canary.json", token)).Transcript;
                    if (string.IsNullOrWhiteSpace(survivingCanary.Text)) throw new InvalidOperationException("Whisper failed and Canary returned no speech text. No transcript can be finalized.");
                    var untimedRegion = new FinalRegion(0, 0, survivingCanary.Text, "", survivingCanary.Text,
                        "single-asr-needs-listening", Confidence: 0, NativeTimestamps: false);
                    await Write(job.Id, "comparison.json", new ComparisonResult([untimedRegion], [],
                        TriAsr.Alignment.TokenAligner.Align([], "", survivingCanary.Language)), token);
                    break;
                }
                if (!File.Exists(FileFor(job.Id, "canary.json")) && (File.Exists(FileFor(job.Id, "Canary/failure.json")) || File.Exists(FileFor(job.Id, "Canary/skipped-language.json"))))
                {
                    var surviving = await Read<EngineTranscript>(job.Id, "whisper.json", token);
                    var fallback = DisagreementDetector.Compare(surviving, surviving with { Text = "", Segments = [] });
                    await Write(job.Id, "comparison.json", fallback with
                    {
                        Regions = fallback.Regions.Select(region => region with { Source = "single-asr-needs-listening", Confidence = 0 }).ToArray(),
                        Disagreements = []
                    }, token);
                    break;
                }
                var whisperSaved = await Read<EngineTranscript>(job.Id, "whisper.json", token);
                var canarySaved = (await Read<CanaryNative.Result>(job.Id, "canary.json", token)).Transcript;
                var comparison = DisagreementDetector.Compare(await DropUnsupportedAsync(job.Id, whisperSaved, canarySaved, seconds, token), canarySaved);
                await Write(job.Id, "comparison.json", comparison, token); break;
            case JobState.Correcting:
                if (File.Exists(FileFor(job.Id, "corrections.json"))) { await Read<Correction[]>(job.Id, "corrections.json", token); return; }
                var disputes = (await Read<ComparisonResult>(job.Id, "comparison.json", token)).Disagreements;
                var decisions = new List<Correction>();
                if (disputes.Count > 0 && !configuration.UseCorrectionModel)
                {
                    // The default: no model changes anybody's words. Whisper's text stays and every disagreement is marked as needing a listen.
                    decisions = disputes.Select(dispute => new Correction(dispute, new("uncertain", dispute.Whisper, 0, true), null)).ToList();
                }
                else if (disputes.Count > 0)
                {
                    await using var arbiter = new LlamaArbiter(runner, paths.CorrectionFor(configuration.CorrectionBackend), paths.CorrectionModel, configuration.CorrectionBackend, governor.Clamp(configuration.CorrectionThreads));
                    try
                    {
                        var correctionEntry = ModelManifest.Entries.First(item => models.PathFor(item) == paths.CorrectionModel);
                        if (!await models.VerifyCachedAsync(correctionEntry, token)) throw new InvalidDataException("Correction model is missing or fails its checksum. Disputes require listening.");
                        await arbiter.StartAsync(token);
                        foreach (var dispute in disputes)
                        {
                            var index = decisions.Count;
                            var checkpoint = $"Corrections/{index:D6}.json";
                            if (File.Exists(FileFor(job.Id, checkpoint))) { decisions.Add(await Read<Correction>(job.Id, checkpoint, token)); continue; }
                            Correction decision;
                            try { decision = new(dispute, await arbiter.ResolveAsync(dispute.Whisper, dispute.Canary, dispute.Before, dispute.After, token), null); }
                            catch (Exception error) when (error is not OperationCanceledException) { decision = new(dispute, new("uncertain", dispute.Whisper, 0, true), error.Message); Issue("Correction needs listening", "A disagreement could not be resolved and is marked uncertain. " + error.Message); }
                            decisions.Add(decision); await Write(job.Id, checkpoint, decision, token);
                            Report(job.Id, stage, (double)decisions.Count / disputes.Count);
                        }
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        Issue("Correction engine failed", "Disagreements are marked uncertain and require listening. " + error.Message);
                        decisions = disputes.Select(dispute => new Correction(dispute, new("uncertain", dispute.Whisper, 0, true), error.Message)).ToList();
                    }
                }
                await Write(job.Id, "corrections.json", decisions, token); break;
            case JobState.Finalizing:
                if (File.Exists(FileFor(job.Id, "final.json"))) { await LoadFinalAsync(job.Id, token); return; }
                var regions = (await Read<ComparisonResult>(job.Id, "comparison.json", token)).Regions.ToArray();
                var corrections = await Read<Correction[]>(job.Id, "corrections.json", token);
                foreach (var group in corrections.GroupBy(item => item.Disagreement.Segment))
                {
                    var region = regions[group.Key]; var text = region.WhisperText;
                    foreach (var item in group.OrderByDescending(item => item.Disagreement.StartCharacter))
                    {
                        if (item.Decision.Uncertain) continue;
                        var d = item.Disagreement;
                        var replacement = item.Decision.Text;
                        if (d.Length == 0 && replacement.Length > 0)
                        {
                            if (d.StartCharacter > 0 && !char.IsWhiteSpace(text[d.StartCharacter - 1])) replacement = " " + replacement;
                            if (d.StartCharacter < text.Length && !char.IsWhiteSpace(text[d.StartCharacter])) replacement += " ";
                        }
                        text = text[..d.StartCharacter] + replacement + text[(d.StartCharacter + d.Length)..];
                    }
                    regions[group.Key] = region with { FinalText = text, Source = group.Any(item => item.Decision.Uncertain) ? "uncertain" : "llm-arbitrated",
                        LlmChoice = string.Join(",", group.Select(item => item.Decision.Choice)), Confidence = group.Min(item => item.Decision.Confidence) };
                }
                var finalLanguage = (await Read<WhisperEngine.LanguageDetection>(job.Id, "language.json", token)).Language;
                var final = TranscriptQuality.FlagRepetition(new FinalTranscript(job.Id, finalLanguage, regions));
                await Write(job.Id, "final.json", final, token);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(stage));
        }
    }
    /// <summary>
    /// Finds the speech in the recording and saves the shared chunk plan (chunks.json). Nothing reads the plan yet, so a missing
    /// detector or a failed run only leaves evidence in the job folder and never stops the job.
    /// </summary>
    public async Task PlanChunksAsync(Guid id, string normalized, int threads, CancellationToken token)
    {
        if (File.Exists(FileFor(id, "chunks.json"))) { await Read<ChunkPlan>(id, "chunks.json", token); return; }
        if (!File.Exists(paths.VadTool) || !File.Exists(paths.VadModel))
        {
            await Write(id, "chunks.skipped.json", new { Reason = "The speech detector or its model is not installed.", Tool = paths.VadTool, Model = paths.VadModel }, token);
            return;
        }
        try
        {
            var milliseconds = (long)Math.Round(WaveAudio.Inspect(normalized).DurationSeconds * 1000);
            var spans = await VadSegmenter.DetectAsync(runner, paths.VadTool, paths.VadModel, normalized, governor.Clamp(Math.Min(4, threads)), token);
            await Write(id, "chunks.json", ChunkPlanner.Plan(milliseconds, spans, ChunkOptions.ForCanary, "silero-v5.1.2"), token);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await Write(id, "chunks.failed.json", new { Error = error.Message, AtUtc = DateTimeOffset.UtcNow }, token);
        }
    }
    /// <summary>
    /// Whisper sometimes repeats itself: one phrase hundreds of times over a stretch without speech, or the same lines again and again
    /// at an impossible speaking speed at the end of a song. The repeats are taken out before the other stages see them (they would only
    /// be compared and sent to the correction model); the first copy stays, and Whisper's own output (Whisper/raw.json) is untouched.
    /// What was removed is listed in Whisper/loops.json.
    /// </summary>
    private async Task<EngineTranscript> RemoveLoopsAsync(Guid id, EngineTranscript transcript, bool alreadySkipping, CancellationToken token)
    {
        var (afterLoops, runs) = LoopGuard.Remove(transcript.Segments);
        var (kept, fastRuns) = RateGuard.Remove(afterLoops);
        if (runs.Count == 0 && fastRuns.Count == 0) return transcript;
        var removed = runs.Sum(run => run.Removed) + fastRuns.Sum(run => run.Removed);
        var minutes = (runs.Sum(run => run.EndMs - run.StartMs) + fastRuns.Sum(run => run.EndMs - run.StartMs)) / 60000d;
        await Write(id, "Whisper/loops.json", new
        {
            Reason = "Whisper repeated itself. Runs: the same segment, or the same few segments in the same order, written again and again back to back (LoopGuard). "
                + "FastRuns: lines written again at an impossible speaking speed, all with the same collapsed length (RateGuard). The first copy of each line is kept.",
            Removed = removed, Runs = runs, FastRuns = fastRuns, RawOutput = "Whisper/raw.json"
        }, token);
        Issue("Repeated text removed", $"Whisper repeated itself: {removed} segments were removed over {minutes:0.#} minutes of the recording, a known failure over stretches without speech or at the end of a song. "
            + "The repeats were removed and Whisper's raw output is kept in the project folder, but speech inside that stretch may be missing from the transcript."
            + (alreadySkipping ? "" : " Turning on \"Skip silence and music\" in Settings usually avoids this and finds that speech."));
        return transcript with { Segments = kept, Text = string.Join(" ", kept.Select(segment => segment.Text)) };
    }    /// <summary>
    /// Text that only Whisper wrote, in a stretch where the speech detector found no speech and Canary wrote nothing like it, is left out of the
    /// comparison (see UnsupportedTextGuard). It needs both engines and the chunk plan; whisper.json is not changed and the removed segments
    /// are listed in Whisper/unsupported.json.
    /// </summary>
    private async Task<EngineTranscript> DropUnsupportedAsync(Guid id, EngineTranscript whisper, EngineTranscript canary, double seconds, CancellationToken token)
    {
        var plan = await TryReadPlanAsync(id, token);
        if (plan is null || plan.Version != ChunkPlan.CurrentVersion || Math.Abs(plan.DurationMs - seconds * 1000) > 2) return whisper;
        var (kept, removed) = UnsupportedTextGuard.Remove(whisper.Segments, canary.Text, plan.Chunks);
        if (removed.Count == 0) return whisper;
        await Write(id, "Whisper/unsupported.json", new
        {
            Reason = "Whisper wrote these segments in stretches where the speech detector found no speech, and fewer than half of their words occur in Canary's text (see UnsupportedTextGuard).",
            Removed = removed, RawOutput = "Whisper/raw.json"
        }, token);
        var words = removed.Sum(item => item.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        Issue("Unsupported text left out", $"{removed.Count} segments ({words} words) that only Whisper wrote, in stretches where no speech was detected and that Canary did not hear, were left out of the transcript. They are kept in the project folder.");
        return whisper with { Segments = kept, Text = string.Join(' ', kept.Select(segment => segment.Text)) };
    }
    private async Task<ChunkPlan?> TryReadPlanAsync(Guid id, CancellationToken token)
    {
        if (!File.Exists(FileFor(id, "chunks.json"))) return null;
        try { return await Read<ChunkPlan>(id, "chunks.json", token); }
        catch (Exception error) when (error is InvalidDataException or JsonException or IOException) { return null; }
    }
    /// <summary>The speech detector model for Whisper's own skipping, or null when this job does not skip (or cannot).</summary>
    private async Task<string?> SkipModelAsync(Guid id, JobConfiguration configuration, string engine, CancellationToken token)
    {
        if (!configuration.SkipNonSpeech) return null;
        // Both engines skip only when the plan exists, so they always leave out the same stretches.
        if (File.Exists(paths.VadModel) && File.Exists(FileFor(id, "chunks.json"))) return paths.VadModel;
        await SkipUnavailableAsync(id, engine, token);
        return null;
    }
    private async Task SkipUnavailableAsync(Guid id, string engine, CancellationToken token)
    {
        await Write(id, engine + "/skip-unavailable.json", new { Reason = "The speech detector is not available, so nothing was skipped.", AtUtc = DateTimeOffset.UtcNow }, token);
        Issue("Silence could not be skipped", $"The speech detector is not available for {engine}, so its whole recording is transcribed.");
    }
    private async Task<JobConfiguration> GetConfiguration(CancellationToken token)
    {
        HardwareProfile? hardware = null;
        var profile = Path.Combine(storage.Root, "Config", "hardware-profile.json");
        if (File.Exists(profile)) hardware = JsonSerializer.Deserialize<HardwareProfile>(await File.ReadAllTextAsync(profile, token));
        var gpu = hardware?.Gpus.MaxBy(item => item.DedicatedBytes);
        var weights = new[] { paths.WhisperModel, paths.CanaryModel, paths.CorrectionModel }.Where(File.Exists).Select(path => (ulong)new FileInfo(path).Length).DefaultIfEmpty(0UL).Max();
        var backend = hardware?.VulkanDevices.Count > 0 && GpuMemoryPlanner.Fits(gpu?.DedicatedBytes ?? 0, weights, 1024UL * 1024 * 1024, 512UL * 1024 * 1024, 1024UL * 1024 * 1024) ? "vulkan" : "cpu";
        var threads = Math.Clamp(hardware?.Topology.PerformanceCores ?? Environment.ProcessorCount / 2, 1, 12);
        var hash = paths.ConfigurationFingerprint(hardware?.Fingerprint ?? "unknown");
        foreach (var path in new[] { paths.WhisperModel }.Concat(File.Exists(paths.CanaryModel) ? new[] { paths.CanaryModel } : []))
        {
            var entry = ModelManifest.Entries.First(item => models.PathFor(item) == path);
            if (!await models.VerifyCachedAsync(entry, token)) throw new InvalidDataException($"Install or verify {entry.Name} {entry.Quantization} on the Models page.");
        }
        var active = await ExecutionSettingsStore.LoadAsync(storage.Root);
        if (active?.Fingerprint == hash)
        {
            return new(hash, active.WhisperBackend, active.WhisperThreads, active.CanaryBackend, active.CanaryThreads, active.CorrectionBackend, active.CorrectionThreads, active.ParallelSpeech);
        }
        return new(hash, backend, threads, backend, threads, backend, threads, false);
    }
    public async Task<bool> CanRunSpeechParallelAsync(TranscriptionJob job, CancellationToken token) =>
        (await Read<JobConfiguration>(job.Id, "configuration.json", token)).ParallelSpeech;
    public async Task<bool> RecoverSpeechFailureAsync(TranscriptionJob job, JobState stage, Exception error, CancellationToken token)
    {
        // Aligning runs after both attempts finish. It checks surviving evidence,
        // and explicitly marks Canary-only output as untimed.
        if (stage is not (JobState.RunningCanary or JobState.RunningWhisper)) return false;
        var engine = stage == JobState.RunningCanary ? "Canary" : "Whisper";
        await Write(job.Id, engine + "/failure.json", new { Error = error.Message, AtUtc = DateTimeOffset.UtcNow, ReviewRequired = true }, token);
        Issue(engine + " transcription failed", "The other engine will be used if it succeeds. All resulting regions require listening. " + error.Message);
        return true;
    }
    public Task<FinalTranscript> LoadFinalAsync(Guid id, CancellationToken token = default) => Read<FinalTranscript>(id, "final.json", token);
    public async Task SaveManualAsync(FinalTranscript transcript, CancellationToken token = default)
    {
        // Keep the immutable machine result and append each complete manual revision separately.
        await Write(transcript.JobId, $"Revisions/{DateTimeOffset.UtcNow.UtcTicks}-{Guid.NewGuid():N}.json", transcript, token);
        await Write(transcript.JobId, "edited.json", transcript, token);
    }
    public async Task<FinalTranscript> LoadReviewAsync(Guid id, CancellationToken token = default) =>
        File.Exists(FileFor(id, "edited.json")) ? await Read<FinalTranscript>(id, "edited.json", token) : await LoadFinalAsync(id, token);
}
