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
    private void Report(Guid id, JobState stage, double fraction, string? label = null) => StageProgressChanged?.Invoke(this, new(id, stage, fraction, label));
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
                throw new InvalidDataException(Loc.T("The source file changed. Create a new job to keep its evidence consistent."));
            var saved = File.Exists(FileFor(job.Id, "configuration.json")) ? await Read<JobConfiguration>(job.Id, "configuration.json", token) : null;
            var preferences = saved is null ? await settings.LoadAsync() : null;
            var config = JobConfiguration.Bind(await GetConfiguration(token), saved, preferences?.SkipNonSpeech ?? false, preferences?.UseCorrectionModel ?? true);
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
                if (LanguageCatalog.TryParseChoice(job.Language, out var pair) && pair.Count > 1)
                {
                    try { detected = await DetectPairAsync(job.Id, whisper, normalized, seconds, pair, configuration.WhisperBackend, "Language", token); }
                    catch (Exception error) when (error is not OperationCanceledException && configuration.WhisperBackend != "cpu")
                    {
                        await Write(job.Id, "Language/fallback.json", new { Error = error.Message, Requested = configuration.WhisperBackend, Retry = "cpu" }, token);
                        Issue(Loc.T("GPU language detection failed"), Loc.T("Retrying on CPU. {0}", Loc.Describe(error.Message)));
                        detected = await DetectPairAsync(job.Id, new WhisperEngine(runner, paths.Whisper, paths.WhisperModel, governor.Clamp(configuration.WhisperThreads)), normalized, seconds, pair, "cpu", "LanguageCpu", token);
                    }
                    await Write(job.Id, "language.json", detected, token); break;
                }
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
                    Issue(Loc.T("GPU language detection failed"), Loc.T("Retrying on CPU. {0}", Loc.Describe(error.Message)));
                    detected = await new WhisperEngine(runner, paths.Whisper, paths.WhisperModel, governor.Clamp(configuration.WhisperThreads)).DetectLanguageAsync(normalized, seconds, FileFor(job.Id, "LanguageCpu"), "cpu", paths.Ffmpeg, token, sampleStarts);
                }
                if (!LanguageCatalog.Supports(detected.Language)) throw new InvalidDataException(Loc.T("Detected unsupported language '{0}'. Choose a language listed in Languages.", detected.Language));
                if (detected.Confidence < .65) throw new InvalidDataException(Loc.T("Language detection is uncertain. Create a job with an explicit speech language."));
                await Write(job.Id, "language.json", detected, token); break;
            case JobState.RunningWhisper:
                if (File.Exists(FileFor(job.Id, "whisper.json"))) { await Read<EngineTranscript>(job.Id, "whisper.json", token); return; }
                var detection = await Read<WhisperEngine.LanguageDetection>(job.Id, "language.json", token);
                var language = detection.Language;
                EngineTranscript first;
                var vadModel = await SkipModelAsync(job.Id, configuration, "Whisper", token);
                // A recording in two languages is read block by block, each block in its own language; any other is read whole, as before.
                Task<EngineTranscript> TranscribeWith(WhisperEngine engine, string folder, string backend) => detection.Blocks is { Count: > 1 } blocks
                    ? TranscribeBlocksAsync(job.Id, engine, normalized, seconds, language, blocks, folder, backend, vadModel, token)
                    : engine.TranscribeAsync(normalized, seconds, language, FileFor(job.Id, folder), backend, token, value => Report(job.Id, stage, value), vadModel);
                try { first = await TranscribeWith(whisper, "Whisper", configuration.WhisperBackend); }
                catch (Exception error) when (error is not OperationCanceledException && configuration.WhisperBackend != "cpu")
                {
                    await Write(job.Id, "Whisper/fallback.json", new { Error = error.Message, Requested = configuration.WhisperBackend, Retry = "cpu" }, token);
                    Issue(Loc.T("Whisper GPU attempt failed"), Loc.T("Retrying on CPU. {0}", Loc.Describe(error.Message)));
                    first = await TranscribeWith(new WhisperEngine(runner, paths.Whisper, paths.WhisperModel, governor.Clamp(configuration.WhisperThreads)), "WhisperCpu", "cpu");
                }
                first = await RemoveLoopsAsync(job.Id, first, configuration.SkipNonSpeech, token);
                await Write(job.Id, "whisper.json", first, token); break;
            case JobState.RunningCanary:
                if (File.Exists(FileFor(job.Id, "canary.json"))) { await Read<CanaryNative.Result>(job.Id, "canary.json", token); return; }
                var spoken = await Read<WhisperEngine.LanguageDetection>(job.Id, "language.json", token);
                var speechLanguage = spoken.Language;
                LanguageCatalog.TryParseChoice(speechLanguage, out var spokenCodes);
                if (spokenCodes.Count == 0 || spokenCodes.Any(code => !LanguageCatalog.CanaryCodes.Contains(code)))
                {
                    await Write(job.Id, "Canary/skipped-language.json", new { Language = speechLanguage, Reason = "Outside Canary's 25-language coverage", ReviewRequired = true }, token);
                    break;
                }
                if (!File.Exists(paths.CanaryModel))
                    throw new FileNotFoundException(Loc.T("Selected Canary model {0} is not downloaded. Open Models and download it, or choose the Balanced preset to use the installed Q8 model.", Path.GetFileName(paths.CanaryModel)), paths.CanaryModel);
                CanaryNative.Result second;
                if (spoken.Blocks is { Count: > 1 } spokenBlocks)
                {
                    // A recording in two languages: Canary reads the speech of each language in that language, and the two readings are put back in time order.
                    var pairPlan = await PlanForPairAsync(job.Id, seconds, token);
                    var parts = new List<CanaryNative.Result>();
                    foreach (var code in spokenCodes)
                    {
                        var windows = LanguageBlocks.Windows(pairPlan, spokenBlocks, code);
                        if (windows.Count > 0) parts.Add(await RunCanaryAsync(job.Id, configuration, normalized, code, windows, $"Canary/{code}", $"CanaryCpu/{code}", token));
                    }
                    if (parts.Count == 0)
                    {
                        await Write(job.Id, "Canary/skipped-language.json", new { Language = speechLanguage, Reason = "No speech windows in either language", ReviewRequired = true }, token);
                        break;
                    }
                    var segments = parts.SelectMany(part => part.Transcript.Segments).OrderBy(segment => segment.StartMs).ToArray();
                    second = new(parts[0].Transcript with
                    {
                        Language = speechLanguage, AudioSeconds = seconds, InferenceSeconds = parts.Sum(part => part.Transcript.InferenceSeconds),
                        Segments = segments, Text = string.Join(" ", segments.Select(segment => segment.Text))
                    }, parts.SelectMany(part => part.RawChunks).ToArray(), parts[0].NativeBackend);
                }
                else
                {
                    // A job that skips silence and music gives Canary the speech windows of its chunk plan (cut in real pauses, finished windows
                    // are kept for a restart). Every other job reads the whole file exactly as before, so its result does not change.
                    IReadOnlyList<CanaryWindow>? windows = null;
                    if (configuration.SkipNonSpeech)
                    {
                        var chunkPlan = await TryReadPlanAsync(job.Id, token);
                        windows = chunkPlan is null ? null : CanaryWindow.SpeechWindows(chunkPlan, (long)Math.Round(seconds * 1000));
                        if (windows is null) await SkipUnavailableAsync(job.Id, "Canary", token);
                    }
                    second = await RunCanaryAsync(job.Id, configuration, normalized, speechLanguage, windows, "Canary", "CanaryCpu", token);
                }
                await Write(job.Id, "canary.json", second, token); break;
            case JobState.Aligning:
                if (File.Exists(FileFor(job.Id, "comparison.json"))) { await Read<ComparisonResult>(job.Id, "comparison.json", token); return; }
                if (!File.Exists(FileFor(job.Id, "whisper.json")))
                {
                    if (!File.Exists(FileFor(job.Id, "canary.json"))) throw new InvalidOperationException(Loc.T("Both speech engines failed. Their failure evidence is retained in the job folder."));
                    var survivingCanary = (await Read<CanaryNative.Result>(job.Id, "canary.json", token)).Transcript;
                    if (string.IsNullOrWhiteSpace(survivingCanary.Text)) throw new InvalidOperationException(Loc.T("Whisper failed and Canary returned no speech text. No transcript can be finalized."));
                    var untimedRegion = new FinalRegion(0, 0, survivingCanary.Text, "", survivingCanary.Text,
                        "single-asr-needs-listening", Confidence: 0, NativeTimestamps: false);
                    await Write(job.Id, "comparison.json", new ComparisonResult([untimedRegion], [],
                        TriAsr.Alignment.TokenAligner.Align([], "", survivingCanary.Language)), token);
                    break;
                }
                var speakerTurns = await FindSpeakersAsync(job.Id, configuration, normalized, token);
                if (!File.Exists(FileFor(job.Id, "canary.json")) && (File.Exists(FileFor(job.Id, "Canary/failure.json")) || File.Exists(FileFor(job.Id, "Canary/skipped-language.json"))))
                {
                    var surviving = await WithSpeakersAsync(job.Id, await Read<EngineTranscript>(job.Id, "whisper.json", token), speakerTurns, token);
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
                var comparison = DisagreementDetector.Compare(await WithSpeakersAsync(job.Id, await DropUnsupportedAsync(job.Id, whisperSaved, canarySaved, seconds, token), speakerTurns, token), canarySaved);
                await Write(job.Id, "comparison.json", comparison, token); break;
            case JobState.Correcting:
                if (File.Exists(FileFor(job.Id, "corrections.json"))) { await Read<Correction[]>(job.Id, "corrections.json", token); return; }
                var disputes = (await Read<ComparisonResult>(job.Id, "comparison.json", token)).Disagreements;
                var decisions = new List<Correction>();
                // The setting asks for the model; a job still runs when the model is not downloaded yet (it behaves as if the setting were off).
                if (disputes.Count > 0 && !(configuration.UseCorrectionModel && File.Exists(paths.CorrectionModel)))
                {
                    // No model changes anybody's words. Whisper's text stays and every disagreement is marked as needing a listen.
                    decisions = disputes.Select(dispute => new Correction(dispute, new("uncertain", dispute.Whisper, 0, true), null)).ToList();
                }
                else if (disputes.Count > 0)
                {
                    await using var arbiter = new LlamaArbiter(runner, paths.CorrectionFor(configuration.CorrectionBackend), paths.CorrectionModel, configuration.CorrectionBackend, governor.Clamp(configuration.CorrectionThreads));
                    try
                    {
                        var correctionEntry = ModelManifest.Entries.First(item => models.PathFor(item) == paths.CorrectionModel);
                        if (!await models.VerifyCachedAsync(correctionEntry, token)) throw new InvalidDataException(Loc.T("Correction model is missing or fails its checksum. Disputes require listening."));
                        await arbiter.StartAsync(token);
                        foreach (var dispute in disputes)
                        {
                            var index = decisions.Count;
                            var checkpoint = $"Corrections/{index:D6}.json";
                            if (File.Exists(FileFor(job.Id, checkpoint))) { decisions.Add(await Read<Correction>(job.Id, checkpoint, token)); continue; }
                            Correction decision;
                            try { decision = new(dispute, await arbiter.ResolveAsync(dispute.Whisper, dispute.Canary, dispute.Before, dispute.After, token), null); }
                            catch (Exception error) when (error is not OperationCanceledException) { decision = new(dispute, new("uncertain", dispute.Whisper, 0, true), error.Message); Issue(Loc.T("Correction needs listening"), Loc.T("A disagreement could not be resolved and is marked uncertain. {0}", Loc.Describe(error.Message))); }
                            decisions.Add(decision); await Write(job.Id, checkpoint, decision, token);
                            Report(job.Id, stage, (double)decisions.Count / disputes.Count);
                        }
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        Issue(Loc.T("Correction engine failed"), Loc.T("Disagreements are marked uncertain and require listening. {0}", Loc.Describe(error.Message)));
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
    /// Tells the speakers of the recording apart, when the job asks for it (options.json): the speaker program finds the turns (Speakers/found.json), they are tidied
    /// and numbered (speakers.json, which a resumed job reads again). Null when the job does not ask, or when it cannot be done: the transcript is then made without
    /// speakers and the person is told why.
    /// </summary>
    private async Task<IReadOnlyList<SpeakerTurn>?> FindSpeakersAsync(Guid id, JobConfiguration configuration, string normalized, CancellationToken token)
    {
        var options = await OptionsAsync(id, token);
        if (!options.TellsSpeakersApart) return null;
        if (File.Exists(FileFor(id, "speakers.json"))) return await Read<SpeakerTurn[]>(id, "speakers.json", token);
        if (!paths.CanTellSpeakersApart)
        {
            await Write(id, "Speakers/unavailable.json", new { Reason = "The speaker program or its models are not installed.", Tool = paths.SpeakersTool }, token);
            Issue(Loc.T("Speakers could not be told apart"), Loc.T("The speaker program is missing. Reinstall Mockingbird Studio. The transcript is made without speakers."));
            return null;
        }
        try
        {
            Report(id, JobState.Aligning, 0, TranscriptionProgressTracker.SpeakerStage);
            var found = await SpeakerFinder.FindAsync(runner, paths.SpeakersTool, paths.SpeakerSegmentationModel, paths.SpeakerEmbeddingModel, normalized,
                governor.Clamp(configuration.WhisperThreads), token, value => Report(id, JobState.Aligning, value * 0.9, TranscriptionProgressTracker.SpeakerStage));
            await Write(id, "Speakers/found.json", found, token);
            var turns = SpeakerTurns.Tidy(found, options.SpeakerCount);
            await Write(id, "speakers.json", turns, token);
            return turns;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await Write(id, "Speakers/failure.json", new { Error = error.Message, AtUtc = DateTimeOffset.UtcNow }, token);
            Issue(Loc.T("Speakers could not be told apart"), Loc.T("The transcript is made without speakers. {0}", Loc.Describe(error.Message)));
            return null;
        }
        finally { Report(id, JobState.Aligning, 0.9, TranscriptionProgressTracker.StageName(JobState.Aligning)); }
    }

    /// <summary>What was chosen for the job beyond its language (nothing, for a job without options.json).</summary>
    private async Task<JobOptions> OptionsAsync(Guid id, CancellationToken token)
    {
        var path = FileFor(id, JobWorkspace.OptionsFile);
        if (!File.Exists(path)) return new JobOptions();
        try
        {
            var options = JsonSerializer.Deserialize<JobOptions>(await File.ReadAllTextAsync(path, token)) ?? new JobOptions();
            return JobOptions.NormalizeSpeakers(options.Speakers) is { } speakers ? options with { Speakers = speakers } : new JobOptions();
        }
        catch (JsonException) { return new JobOptions(); }
    }

    /// <summary>
    /// Gives Whisper's segments their speakers, cut where the speaker changes; the words and their times come from Whisper's own output (block by block for a
    /// recording in two languages, from the processor's output when the graphics card failed). Without speakers the segments stay as they are.
    /// </summary>
    private async Task<EngineTranscript> WithSpeakersAsync(Guid id, EngineTranscript whisper, IReadOnlyList<SpeakerTurn>? turns, CancellationToken token)
    {
        if (turns is not { Count: > 0 }) return whisper;
        var folder = File.Exists(FileFor(id, "Whisper/fallback.json")) ? "WhisperCpu" : "Whisper";
        var detection = File.Exists(FileFor(id, "language.json")) ? await Read<WhisperEngine.LanguageDetection>(id, "language.json", token) : null;
        var outputs = detection?.Blocks is { Count: > 1 } blocks
            ? blocks.Select((block, i) => (Path: FileFor(id, $"{folder}/Block{i:D3}/raw.json"), Offset: block.StartMs)).ToList()
            : [(Path: FileFor(id, $"{folder}/raw.json"), Offset: 0L)];
        var words = new List<TimedWord>();
        foreach (var (path, offset) in outputs)
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
                words.AddRange(WhisperEngine.ParseWords(document.RootElement, offset));
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { }
        }
        var segments = SpeakerTurns.Split(whisper.Segments, words, turns);
        return whisper with { Segments = segments };
    }

    /// <summary>
    /// Runs Canary once (in <paramref name="folder"/>: its request, raw output and the program's messages), and once more on the processor when the graphics card fails.
    /// <paramref name="windows"/> null reads the whole file in Canary's own windows.
    /// </summary>
    private async Task<CanaryNative.Result> RunCanaryAsync(Guid id, JobConfiguration configuration, string normalized, string language, IReadOnlyList<CanaryWindow>? windows,
        string folder, string cpuFolder, CancellationToken token)
    {
        var raw = FileFor(id, $"{folder}/raw.json"); Directory.CreateDirectory(Path.GetDirectoryName(raw)!);
        var request = new CanaryRequest(paths.CanaryFor(configuration.CanaryBackend), paths.CanaryModel, normalized, language,
            configuration.CanaryBackend, governor.Clamp(configuration.CanaryThreads), raw, windows, windows is null ? null : FileFor(id, $"{folder}/windows"));
        await Write(id, $"{folder}/request.json", request, token);
        var result = await runner.RunAsync(new(paths.CanaryWorker, ["--canary", FileFor(id, $"{folder}/request.json")],
            Path.GetDirectoryName(paths.CanaryWorker)!, TimeSpan.FromHours(12), line => CanaryProgress(id, line)), token);
        await File.WriteAllTextAsync(FileFor(id, $"{folder}/runtime.stderr.txt"), result.StandardError, token);
        if (result.ExitCode != 0 && configuration.CanaryBackend != "cpu")
        {
            await Write(id, $"{folder}/fallback.json", new { Error = result.StandardError, Requested = configuration.CanaryBackend, Retry = "cpu" }, token);
            Issue(Loc.T("Canary GPU attempt failed"), Loc.T("Retrying on CPU. The attempt output is saved in the job folder."));
            request = request with { RuntimeDirectory = paths.CanaryRuntime, Backend = "cpu", Output = FileFor(id, $"{cpuFolder}/raw.json") };
            Directory.CreateDirectory(FileFor(id, cpuFolder));
            await Write(id, $"{cpuFolder}/request.json", request, token);
            result = await runner.RunAsync(new(paths.CanaryWorker, ["--canary", FileFor(id, $"{cpuFolder}/request.json")],
                Path.GetDirectoryName(paths.CanaryWorker)!, TimeSpan.FromHours(12), line => CanaryProgress(id, line)), token);
            await File.WriteAllTextAsync(FileFor(id, $"{cpuFolder}/runtime.stderr.txt"), result.StandardError, token);
        }
        if (result.ExitCode != 0) throw new InvalidOperationException(Loc.T("Canary failed. Whisper evidence is saved; resume after fixing the runtime. {0}", result.StandardError[^Math.Min(500, result.StandardError.Length)..]));
        var read = await Read<CanaryNative.Result>(id, request.Backend == "cpu" && configuration.CanaryBackend != "cpu" ? $"{cpuFolder}/raw.json" : $"{folder}/raw.json", token);
        if (read.Transcript.ActualBackend != request.Backend) throw new InvalidDataException(Loc.T("Canary backend mismatch."));
        return read;
    }

    /// <summary>
    /// The chunk plan a recording in two languages is divided by: the saved one when it describes this recording, otherwise stretches of about 20 s (the speech
    /// detector is not installed or failed). The same plan is found again by every stage, so they agree on the stretches.
    /// </summary>
    private async Task<ChunkPlan> PlanForPairAsync(Guid id, double seconds, CancellationToken token)
    {
        var milliseconds = (long)Math.Round(seconds * 1000);
        var plan = await TryReadPlanAsync(id, token);
        return plan is not null && plan.Version == ChunkPlan.CurrentVersion && Math.Abs(plan.DurationMs - milliseconds) <= 2 ? plan : LanguageBlocks.Even(milliseconds);
    }

    /// <summary>
    /// Finds out which of the two languages chosen each speech stretch of the recording is in (the rule of live dictation: detected when the program is sure,
    /// otherwise read in both and the more confident reading wins) and divides the recording into blocks of one language. What was decided is saved in
    /// Language/pair.json. A recording that turns out to be in one language only is treated as if that language had been chosen.
    /// </summary>
    private async Task<WhisperEngine.LanguageDetection> DetectPairAsync(Guid id, WhisperEngine whisper, string normalized, double seconds, IReadOnlyList<string> codes, string backend, string evidence, CancellationToken token)
    {
        var plan = await PlanForPairAsync(id, seconds, token);
        var folder = FileFor(id, $"{evidence}/Pair");
        var speech = plan.SpeechChunks().ToList();
        var files = speech.Select(chunk => Path.Combine(folder, $"{chunk.Index:D5}.wav")).ToList();
        try
        {
            for (var i = 0; i < speech.Count; i++) WaveSlice.Write(normalized, speech[i].StartMs, speech[i].EndMs, files[i]);
            var detected = speech.Count == 0 ? [] : await whisper.DetectEachAsync(files, backend, token, value => Report(id, JobState.DetectingLanguage, value / 3));
            var detections = speech.Select((chunk, i) => new ChunkDetection(chunk.Index, detected[i].Language, detected[i].Detection)).ToList();
            var unsure = Enumerable.Range(0, detections.Count).Where(i => !LiveLanguagePicker.IsSure(detections[i].Language, detections[i].Detection, codes)).ToList();
            var readings = unsure.ToDictionary(i => detections[i].Index, _ => new List<SpeechReading>());
            for (var c = 0; c < codes.Count && unsure.Count > 0; c++)
            {
                var read = await whisper.ReadEachAsync(unsure.Select(i => files[i]).ToList(), codes[c], Path.Combine(folder, "Readings"), backend, token);
                for (var k = 0; k < unsure.Count; k++) readings[detections[unsure[k]].Index].Add(read[k]);
                Report(id, JobState.DetectingLanguage, (1 + c + 1) / (double)(codes.Count + 1));
            }
            var languages = LanguageBlocks.Decide(detections, readings.ToDictionary(item => item.Key, item => (IReadOnlyList<SpeechReading>)item.Value), codes);
            var blocks = LanguageBlocks.From(plan, languages, codes[0]);
            var present = LanguageBlocks.Present(blocks, codes);
            await Write(id, $"{evidence}/pair.json", new
            {
                Choice = LanguageCatalog.JoinChoice(codes), Plan = plan.Source,
                Stretches = speech.Select((chunk, i) => new
                {
                    chunk.Index, chunk.StartMs, chunk.EndMs, Detected = detections[i].Language, detections[i].Detection,
                    Readings = readings.TryGetValue(chunk.Index, out var read) ? read.Select(item => new { item.Language, item.Confidence, item.Text }).ToArray() : null,
                    Language = languages[chunk.Index]
                }),
                Blocks = blocks
            }, token);
            return present.Count > 1
                ? new(LanguageCatalog.JoinChoice(present), 1, speech.Select(chunk => languages[chunk.Index]).ToArray(), blocks)
                : new(present.Count == 1 ? present[0] : codes[0], 1, speech.Select(chunk => languages[chunk.Index]).ToArray());
        }
        finally
        {
            foreach (var file in files) try { File.Delete(file); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Reads a recording in two languages block by block, each block in its own language, and puts the segments back on the recording's timeline.
    /// Each block's own output stays in <paramref name="folder"/>/Block000 and on.
    /// </summary>
    private async Task<EngineTranscript> TranscribeBlocksAsync(Guid id, WhisperEngine whisper, string normalized, double seconds, string choice, IReadOnlyList<LanguageBlock> blocks,
        string folder, string backend, string? vadModel, CancellationToken token)
    {
        var segments = new List<TranscriptSegment>();
        EngineTranscript? template = null;
        double inference = 0, done = 0, total = Math.Max(1, blocks.Sum(block => block.DurationMs));
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var directory = FileFor(id, $"{folder}/Block{i:D3}");
            var audio = Path.Combine(directory, "block.wav");
            WaveSlice.Write(normalized, block.StartMs, block.EndMs, audio);
            try
            {
                var before = done;
                var part = await whisper.TranscribeAsync(audio, block.DurationMs / 1000d, block.Language, directory, backend, token,
                    value => Report(id, JobState.RunningWhisper, (before + value * block.DurationMs) / total), vadModel);
                template ??= part;
                inference += part.InferenceSeconds;
                segments.AddRange(part.Segments.Select(segment => segment with { StartMs = segment.StartMs + block.StartMs, EndMs = segment.EndMs + block.StartMs }));
            }
            finally { try { File.Delete(audio); } catch (IOException) { } }
            done += block.DurationMs;
        }
        return template! with { Language = choice, AudioSeconds = seconds, InferenceSeconds = inference, Segments = segments, Text = string.Join(" ", segments.Select(segment => segment.Text)) };
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
        Issue(Loc.T("Repeated text removed"), Loc.T("Whisper repeated itself. Segments removed: {0}. Recording time covered: {1:0.#} min. This is a known failure over stretches without speech or at the end of a song.", removed, minutes)
            + " " + Loc.T("The repeats were removed and Whisper's raw output is kept in the project folder, but speech inside that stretch may be missing from the transcript.")
            + (alreadySkipping ? "" : " " + Loc.T("Turning on \"Skip silence and music\" in Settings can prevent this, because stretches without speech are then not transcribed.")));
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
        Issue(Loc.T("Unsupported text left out"), Loc.T("Segments that only Whisper wrote, in stretches where no speech was detected and that Canary did not hear, were left out of the transcript (segments: {0}, words: {1}). They are kept in the project folder.", removed.Count, words));
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
        Issue(Loc.T("Silence could not be skipped"), Loc.T("The speech detector is not available for {0}, so its whole recording is transcribed.", engine));
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
            if (!await models.VerifyCachedAsync(entry, token)) throw new InvalidDataException(Loc.T("Install or verify {0} {1} on the Models page.", entry.Name, entry.Quantization));
        }
        var active = await ExecutionSettingsStore.LoadAsync(storage.Root);
        if (active?.Fingerprint == hash)
        {
            return new(hash, active.WhisperBackend, active.WhisperThreads, active.CanaryBackend, active.CanaryThreads, active.CorrectionBackend, active.CorrectionThreads, active.ParallelSpeech);
        }
        return new(hash, backend, threads, backend, threads, backend, threads, false);
    }
    /// <summary>
    /// The graphics card or processor, and the number of threads, that a phrase of live dictation is read with: what a transcription would use. The models are not checked
    /// the way a transcription checks them, because dictation reads with whichever Whisper model is installed.
    /// </summary>
    public async Task<(string Backend, int Threads)> LiveExecutionAsync(string model, CancellationToken token)
    {
        HardwareProfile? hardware = null;
        var profile = Path.Combine(storage.Root, "Config", "hardware-profile.json");
        if (File.Exists(profile)) hardware = JsonSerializer.Deserialize<HardwareProfile>(await File.ReadAllTextAsync(profile, token));
        var gpu = hardware?.Gpus.MaxBy(item => item.DedicatedBytes);
        var weights = File.Exists(model) ? (ulong)new FileInfo(model).Length : 0UL;
        var backend = hardware?.VulkanDevices.Count > 0 && GpuMemoryPlanner.Fits(gpu?.DedicatedBytes ?? 0, weights, 1024UL * 1024 * 1024, 512UL * 1024 * 1024, 1024UL * 1024 * 1024) ? "vulkan" : "cpu";
        var threads = Math.Clamp(hardware?.Topology.PerformanceCores ?? Environment.ProcessorCount / 2, 1, 12);
        var active = await ExecutionSettingsStore.LoadAsync(storage.Root);
        if (active?.Fingerprint == paths.ConfigurationFingerprint(hardware?.Fingerprint ?? "unknown")) return (active.WhisperBackend, active.WhisperThreads);
        return (backend, threads);
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
        Issue(Loc.T("{0} transcription failed", engine), Loc.T("The other engine will be used if it succeeds. All resulting regions require listening. {0}", Loc.Describe(error.Message)));
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
