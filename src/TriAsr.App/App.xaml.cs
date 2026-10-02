using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TriAsr.Application;
using TriAsr.Infrastructure;
using TriAsr.Persistence;
using TriAsr.Audio;
using TriAsr.Hardware;
using TriAsr.Engine.Llm;

namespace TriAsr.App;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    public static IHost CreateHost(string dataRoot, Action<IServiceCollection>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddInfrastructure(dataRoot);
        builder.Services.AddPersistence();
        var runtimes = new RuntimePaths();
        builder.Services.AddSingleton(runtimes);
        builder.Services.AddSingleton(new ModelStore(runtimes.ModelRoot));
        builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
        builder.Services.AddSingleton<IJobWorkspace, JobWorkspace>();
        builder.Services.AddSingleton<IJobRepository, JobRepository>();
        builder.Services.AddSingleton<IRecordRepository, RecordRepository>();
        builder.Services.AddSingleton<IAudioNormalizer>(services => new FfmpegNormalizer(services.GetRequiredService<IProcessRunner>(), runtimes.Ffmpeg));
        builder.Services.AddSingleton<AudioJobQueue>();
        builder.Services.AddSingleton<LocalTranscriptionStages>();
        builder.Services.AddSingleton<ITranscriptionStages>(services => services.GetRequiredService<LocalTranscriptionStages>());
        builder.Services.AddSingleton<TranscriptionPipeline>();
        builder.Services.AddSingleton<LocalOptimizer>();
        builder.Services.AddSingleton<HardwareProfiler>();
        builder.Services.AddSingleton<SettingsStore>();
        builder.Services.AddSingleton<ResourceGovernor>();
        builder.Services.AddSingleton(UpdateOptions.FromEnvironment());
        builder.Services.AddSingleton(services => new UpdateService(services.GetRequiredService<UpdateOptions>()));
        builder.Services.AddSingleton<ThemeManager>();
        builder.Services.AddSingleton<ShellViewModel>();
        builder.Services.AddSingleton<BootstrapViewModel>();
        builder.Services.AddSingleton<BootstrapWindow>();
        builder.Services.AddSingleton<MainWindow>();
        configure?.Invoke(builder.Services);
        return builder.Build();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smoke = e.Args.Contains("--smoke-test", StringComparer.Ordinal);
        try
        {
            var dataRoot = Environment.GetEnvironmentVariable("TRIASR_DATA_ROOT")
                ?? new RuntimePaths().DefaultDataRoot;
            _host = CreateHost(dataRoot);
            await _host.StartAsync();
            var logger = _host.Services.GetRequiredService<ILogger<App>>();
            logger.LogInformation("Application started: {ApplicationVersion} {Architecture}",
                typeof(App).Assembly.GetName().Version, System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
            var shell = _host.Services.GetRequiredService<ShellViewModel>();
            await shell.InitializeAsync();
            await shell.DetectHardwareCommand.ExecuteAsync(null);
            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            if (smoke)
            {
                if (shell.Hardware is null) throw new InvalidOperationException(shell.Diagnostics);
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -20000;
                window.Top = -20000;
                window.Show();
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                if (!window.IsLoaded || new System.Windows.Interop.WindowInteropHelper(window).Handle == IntPtr.Zero)
                    throw new InvalidOperationException("WPF window did not initialize.");
                window.Hide();
                var matrix = await ShellSmoke.RunAsync(window, shell, _host.Services.GetRequiredService<ThemeManager>(),
                    _host.Services.GetRequiredService<SettingsStore>(), dataRoot);
                if (e.Args.Contains("--terminal-smoke"))
                {
                    var terminal = _host.Services.GetRequiredService<InteractiveTerminal>();
                    shell.SelectedPage = shell.Navigation.First(page => page.Name == "Terminal");
                    shell.TerminalInput = "$terminalSmokeValue = 41; Write-Output ($terminalSmokeValue + 1)";
                    await shell.SubmitTerminalCommand.ExecuteAsync(null); await terminal.WaitForIdleAsync(TimeSpan.FromSeconds(10));
                    shell.TerminalInput = "Get-Location; Write-Output ('Saved value: ' + $terminalSmokeValue)";
                    await shell.SubmitTerminalCommand.ExecuteAsync(null); await terminal.WaitForIdleAsync(TimeSpan.FromSeconds(10)); shell.RefreshTerminal();
                    if (!shell.TerminalOutput.Contains("Saved value: 41") || !shell.TerminalOutput.Contains("42")) throw new InvalidOperationException("PowerShell command output did not reach the Terminal panel.");
                    shell.RecallTerminalCommand(-1); if (!shell.TerminalInput.Contains("Get-Location")) throw new InvalidOperationException("Terminal command history was not restored.");
                    shell.TerminalInput = "";
                    ShellSmoke.Capture(window, Path.Combine(dataRoot, "renders", "terminal-powershell.png"), 1220, 900, 1);
                    window.Width = 800; await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    ShellSmoke.Capture(window, Path.Combine(dataRoot, "renders", "terminal-powershell-narrow.png"), 800, 900, 1);
                    var runner = _host.Services.GetRequiredService<IProcessRunner>();
                    await runner.RunAsync(new(InteractiveTerminal.ShellExecutable, ["-NoProfile", "-Command", "[Console]::WriteLine('Terminal activity worker output'); [Console]::Error.WriteLine('Terminal activity stderr')"], dataRoot, TimeSpan.FromSeconds(10)));
                    logger.LogInformation("Terminal activity application log check");
                    shell.ModelProgress = "Terminal activity progress check";
                    shell.TerminalView = "Activity"; shell.RefreshTerminal();
                    if (!shell.TerminalOutput.Contains("Terminal activity worker output") || !shell.TerminalOutput.Contains("Terminal activity stderr") || !shell.TerminalOutput.Contains("Terminal activity application log check") || !shell.TerminalOutput.Contains("Terminal activity progress check"))
                        throw new InvalidOperationException("Activity panel missed native output, logging or UI progress.");
                    window.Width = 1220; await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    ShellSmoke.Capture(window, Path.Combine(dataRoot, "renders", "terminal-activity.png"), 1220, 900, 1);
                    await shell.StopTerminalCommand.ExecuteAsync(null);
                    if (terminal.IsRunning) throw new InvalidOperationException("Terminal session did not stop.");
                    shell.ClearTerminalCommand.Execute(null); if (shell.TerminalOutput.Length != 0) throw new InvalidOperationException("Clear view did not clear the visible activity.");
                    await File.WriteAllTextAsync(Path.Combine(dataRoot, "terminal-smoke.json"), JsonSerializer.Serialize(new { commandOutput = true, statePreserved = true, history = true, nativeStreams = true, appLogs = true, progress = true, clearView = true, stopped = true }));
                }
                if (e.Args.Contains("--audio-smoke"))
                {
                    var source = e.Args.Last();
                    var queue = _host.Services.GetRequiredService<AudioJobQueue>();
                    var job = await queue.EnqueueAsync(source, "auto");
                    job = await queue.PrepareAsync(job);
                    if (job.State == TriAsr.Domain.JobState.Failed || job.Checkpoint != "normalized") throw new InvalidOperationException(job.Error ?? "Normalization failed.");
                    var repository = _host.Services.GetRequiredService<IJobRepository>();
                    if (!(await repository.ListAsync()).Any(saved => saved.Id == job.Id && saved.Checkpoint == "normalized")) throw new InvalidOperationException("Job was not persisted.");
                    await File.WriteAllTextAsync(Path.Combine(dataRoot, "audio-smoke.json"), JsonSerializer.Serialize(job));
                }
                if (e.Args.Contains("--missing-model-smoke"))
                {
                    shell.SelectedPreset = "Maximum Accuracy"; shell.SelectedLanguage = "en"; shell.SourcePath = e.Args.Last();
                    await shell.PrepareAudioCommand.ExecuteAsync(null);
                    if (shell.ErrorTitle != "Selected models are not downloaded" || shell.IsProcessing || shell.Jobs.Count != 0)
                        throw new InvalidOperationException("Missing model setup was not blocked before creating a job.");
                    await File.WriteAllTextAsync(Path.Combine(dataRoot, "missing-model-smoke.json"), JsonSerializer.Serialize(new { blockedBeforeJob = true, shell.ErrorTitle, shell.ErrorMessage }));
                    shell.SelectedPage = shell.Navigation.First(page => page.Name == "New Transcription");
                    ShellSmoke.Capture(window, Path.Combine(dataRoot, "renders", "missing-model-preflight.png"), 1220, 1000, 1);
                    shell.SelectedPreset = "Balanced";
                }
                if (e.Args.Contains("--quality-smoke"))
                {
                    var source = e.Args.Last();
                    var original = JsonSerializer.Deserialize<TriAsr.Domain.FinalTranscript>(await File.ReadAllTextAsync(source))!;
                    var flagged = TriAsr.Fusion.TranscriptQuality.FlagRepetition(original);
                    if (!flagged.Regions.Any(region => region.Warnings?.Count > 0)) throw new InvalidOperationException("Expected repetition loop was not flagged.");
                    for (var index = 0; index < original.Regions.Count; index++)
                        if (original.Regions[index] != (flagged.Regions[index] with { Warnings = original.Regions[index].Warnings }))
                            throw new InvalidOperationException("Quality review modified original transcript evidence.");
                    var job = new TriAsr.Domain.TranscriptionJob(original.JobId, "quality review", original.Language, TriAsr.Domain.JobState.Complete, DateTimeOffset.UtcNow);
                    var directory = _host.Services.GetRequiredService<IJobWorkspace>().DirectoryFor(job.Id); Directory.CreateDirectory(directory);
                    File.Copy(source, Path.Combine(directory, "final.json"));
                    File.Copy(Path.Combine(Path.GetDirectoryName(source)!, "normalized.wav"), Path.Combine(directory, "normalized.wav"));
                    var whisperEvidence = Path.Combine(Path.GetDirectoryName(source)!, "whisper.json");
                    if (File.Exists(whisperEvidence)) File.Copy(whisperEvidence, Path.Combine(directory, "whisper.json"));
                    await _host.Services.GetRequiredService<IJobRepository>().SaveAsync(job);
                    shell.SelectedJob = job; await shell.OpenReviewCommand.ExecuteAsync(null);
                    if (!shell.HasError || shell.ErrorTitle != "Possible transcription repetition loop" || !shell.Regions.Any(region => region.IsUncertain && region.Original.Warnings?.Count > 0))
                        throw new InvalidOperationException("Quality warning did not reach the review UI: " + shell.ErrorTitle + " · " + shell.ErrorMessage);
                    ShellSmoke.Capture(window, Path.Combine(dataRoot, "renders", "repetition-review.png"), 1220, 1000, 1);
                    await File.WriteAllTextAsync(Path.Combine(dataRoot, "quality-smoke.json"), JsonSerializer.Serialize(new { regions = original.Regions.Count, flagged = flagged.Regions.Count(region => region.Warnings?.Count > 0), evidencePreserved = true, reviewWarningShown = true }));
                }
                if (e.Args.Contains("--correction-smoke"))
                {
                    var runtimes = _host.Services.GetRequiredService<RuntimePaths>();
                    await using var arbiter = new LlamaArbiter(_host.Services.GetRequiredService<IProcessRunner>(), runtimes.LlamaServer, runtimes.CorrectionModel, "vulkan", 12);
                    await arbiter.StartAsync(CancellationToken.None);
                    var decision = await arbiter.ResolveAsync("Schulternhalle", "Schulturnhalle", "Ich habe gesehen, dass ein Verein unsere ", " benutzt.", CancellationToken.None);
                    await File.WriteAllTextAsync(Path.Combine(dataRoot, "correction-smoke.json"), JsonSerializer.Serialize(new { arbiter.ActualBackend, decision }));
                }
                if (e.Args.Contains("--pipeline-smoke"))
                {
                    var queue = _host.Services.GetRequiredService<AudioJobQueue>();
                    var pipeline = _host.Services.GetRequiredService<TranscriptionPipeline>();
                    pipeline.JobChanged += (_, job) => logger.LogInformation("Pipeline stage: {State}", job.State);
                    var progressUpdates = new System.Collections.Concurrent.ConcurrentQueue<TranscriptionProgress>();
                    var progressCapture = 0;
                    void ObserveProgress(object? sender, TranscriptionProgress update)
                    {
                        progressUpdates.Enqueue(update);
                        if (update.IsRunning && update.CompletedStages == 2 && update.Percent > 30 &&
                            Interlocked.CompareExchange(ref progressCapture, 1, 0) == 0)
                            Dispatcher.Invoke(() => ShellSmoke.Capture(window,
                                Path.Combine(dataRoot, "renders", "transcription-progress.png"), 1220, 900, 1));
                    }
                    pipeline.ProgressChanged += ObserveProgress;
                    var languageArgument = Array.IndexOf(e.Args, "--speech-language");
                    var smokeLanguage = languageArgument >= 0 && languageArgument + 1 < e.Args.Length ? e.Args[languageArgument + 1] : "auto";
                    // The shell smoke above resets the saved preferences, so the choice has to be made again here.
                    if (e.Args.Contains("--skip-non-speech") || e.Args.Contains("--correction-model"))
                    {
                        var preferences = _host.Services.GetRequiredService<SettingsStore>();
                        var chosen = await preferences.LoadAsync();
                        await preferences.SaveAsync(chosen with { SkipNonSpeech = chosen.SkipNonSpeech || e.Args.Contains("--skip-non-speech"), UseCorrectionModel = chosen.UseCorrectionModel || e.Args.Contains("--correction-model") });
                    }
                    var job = await queue.EnqueueAsync(e.Args.Last(), smokeLanguage);
                    job = await pipeline.RunAsync(job);
                    pipeline.ProgressChanged -= ObserveProgress;
                    var updates = progressUpdates.ToArray();
                    if (updates.Length == 0 || updates[^1].Percent != 100 || updates[^1].IsRunning ||
                        !updates.Any(update => update.CompletedStages == 2 && update.Percent > 30 && update.Percent < 42.8))
                        throw new InvalidOperationException("Native transcription progress did not reach the shell.");
                    await File.WriteAllTextAsync(Path.Combine(dataRoot, "transcription-progress.json"),
                        JsonSerializer.Serialize(updates, new JsonSerializerOptions { WriteIndented = true }));
                    if (job.State != TriAsr.Domain.JobState.Complete) throw new InvalidOperationException(job.Error ?? "Pipeline incomplete.");
                    var stages = _host.Services.GetRequiredService<LocalTranscriptionStages>();
                    var result = await stages.LoadFinalAsync(job.Id);
                    if (!TriAsr.Domain.LanguageCatalog.Supports(result.Language) || result.Regions.Count == 0) throw new InvalidOperationException("Expected a timestamped transcript in a supported language.");
                    if (result.Regions.Any(region => region.CanaryText.Length == 0 && region.WhisperText.Length > 0 && region.Source == "agreement"))
                        throw new InvalidOperationException("One-sided text must enter Review.");
                    shell.SelectedJob = job; await shell.OpenReviewCommand.ExecuteAsync(null);
                    var listeningCopy = Path.Combine(_host.Services.GetRequiredService<IJobWorkspace>().DirectoryFor(job.Id), "playback.m4a");
                    if (!File.Exists(listeningCopy)) throw new InvalidOperationException("The 48 kHz listening copy was not created.");
                    if (!string.Equals(shell.AudioSource?.LocalPath, listeningCopy, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Review is not playing the listening copy.");
                    await window.VerifyAudioPlaybackAsync();
                    foreach (var theme in new[] { "Light", "Dark" })
                    {
                        shell.SelectedTheme = theme; window.Width = 1220;
                        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        ShellSmoke.Capture(window, Path.Combine(dataRoot, "renders", theme + "-review.png"), 1220, 900, 1);
                    }
                    var exportDirectory = Path.Combine(dataRoot, "exports"); Directory.CreateDirectory(exportDirectory);
                    foreach (var extension in new[] { "txt", "md", "srt", "vtt", "csv", "json", "docx" })
                        await TriAsr.Export.TranscriptExporter.SaveAsync(result, Path.Combine(exportDirectory, "transcript." + extension));
                    var text = shell.Regions[0].Text; shell.Regions[0].Text += " [manual smoke]";
                    await shell.SaveReviewCommand.ExecuteAsync(null); await shell.OpenReviewCommand.ExecuteAsync(null);
                    if (shell.Regions[0].Original.Revisions?.Count != 1 || shell.Regions[0].Whisper != result.Regions[0].WhisperText)
                        throw new InvalidOperationException("Manual revision did not persist its evidence.");
                    var jobDirectory = _host.Services.GetRequiredService<IJobWorkspace>().DirectoryFor(job.Id);
                    var original = File.GetLastWriteTimeUtc(Path.Combine(jobDirectory, "whisper.json"));
                    job = await pipeline.RunAsync(job with { State = TriAsr.Domain.JobState.Cancelled });
                    if (job.State != TriAsr.Domain.JobState.Complete || original != File.GetLastWriteTimeUtc(Path.Combine(jobDirectory, "whisper.json")))
                        throw new InvalidOperationException("Resume overwrote completed Whisper evidence.");
                    await File.WriteAllTextAsync(Path.Combine(dataRoot, "pipeline-smoke.json"), JsonSerializer.Serialize(new { job, result, checkpointReused = true, listeningCopyBytes = new FileInfo(listeningCopy).Length }));
                }
                if (e.Args.Contains("--setup-smoke"))
                {
                    var recommended = shell.ModelCards.Where(card => card.Recommended).ToArray();
                    if (recommended.Length != 3 || recommended.Any(card => !File.Exists(card.Location)))
                        throw new InvalidOperationException("Setup smoke requires locally installed recommended models; it never downloads large test fixtures.");
                    await shell.InstallRecommendedCommand.ExecuteAsync(null);
                    if (recommended.Any(card => !card.Selected || !card.Status.Contains("ready offline")))
                        throw new InvalidOperationException("Recommended model setup did not finish.");
                    shell.SelectedExpansionLanguage = TriAsr.Domain.LanguageCatalog.All.First(language => language.Code == "fr");
                    await shell.InstallLanguageSupportCommand.ExecuteAsync(null);
                    using var reloadedHost = CreateHost(dataRoot);
                    var reloadedShell = reloadedHost.Services.GetRequiredService<ShellViewModel>();
                    await reloadedShell.InitializeAsync(); await reloadedShell.DetectHardwareCommand.ExecuteAsync(null);
                    if (reloadedShell.ModelCards.Count(card => card.Selected && card.Recommended && card.Installed && card.Status.Contains("ready offline")) != 3)
                        throw new InvalidOperationException("Model selection, highlight or verification was lost on reload.");
                    if (!reloadedShell.SavedLanguageSummary.Contains("French")) throw new InvalidOperationException("Language expansion setup was lost on reload.");
                    shell.SelectedPage = shell.Navigation.First(page => page.Name == "Models");
                    window.Width = 1220;
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    ShellSmoke.Capture(window, Path.Combine(dataRoot, "renders", "models-ready-offline.png"), 1220, 1000, 1);
                    await File.WriteAllTextAsync(Path.Combine(dataRoot, "setup-smoke.json"), JsonSerializer.Serialize(new
                    { savedModels = recommended.Select(card => card.Entry.Id), restartRestored = true, languageSetupRestored = true, networkDownloadNeeded = false }));
                }
                if (e.Args.Contains("--watch-smoke"))
                {
                    // Real engines and models: drops recordings into a watched folder the way a file copy would and waits for the transcripts to appear beside them.
                    var runner = _host.Services.GetRequiredService<IProcessRunner>();
                    var sample = await TestSpeech.CreateAsync(runner, Path.Combine(dataRoot, "Setup"), CancellationToken.None) ?? throw new InvalidOperationException("This computer has no Windows voice to make a test recording.");
                    var watched = Path.Combine(dataRoot, "watched");
                    Directory.CreateDirectory(watched);
                    File.Copy(sample, Path.Combine(watched, "old.wav"));
                    shell.SetWatchFolder(watched);
                    for (var wait = 0; wait < 100 && !shell.WatchStatus.StartsWith("Watching"); wait++) await Task.Delay(100);
                    if (!shell.WatchStatus.StartsWith("Watching")) throw new InvalidOperationException("Watching did not start: " + shell.WatchStatus);
                    async Task<bool> AppearsAsync(string name, int seconds)
                    {
                        for (var wait = 0; wait < seconds * 5; wait++) { if (File.Exists(Path.Combine(watched, name))) return true; await Task.Delay(200); }
                        return false;
                    }
                    async Task CopyInPiecesAsync(string name)
                    {
                        var bytes = await File.ReadAllBytesAsync(sample);
                        await using var output = new FileStream(Path.Combine(watched, name), FileMode.Create, FileAccess.Write, FileShare.Read);
                        for (var offset = 0; offset < bytes.Length; offset += 64 * 1024) { await output.WriteAsync(bytes.AsMemory(offset, Math.Min(64 * 1024, bytes.Length - offset))); await output.FlushAsync(); await Task.Delay(600); }
                    }
                    var copyStarted = DateTime.UtcNow;
                    await CopyInPiecesAsync("new-meeting.wav");
                    var copySeconds = (DateTime.UtcNow - copyStarted).TotalSeconds;
                    if (!await AppearsAsync("new-meeting.txt", 300)) throw new InvalidOperationException("No transcript appeared next to the new recording. Status: " + shell.WatchStatus + " · " + shell.ErrorMessage);
                    var text = await File.ReadAllTextAsync(Path.Combine(watched, "new-meeting.txt"));
                    if (text.Trim().Length < 40) throw new InvalidOperationException("The transcript is nearly empty: " + text);
                    if (File.Exists(Path.Combine(watched, "old.txt"))) throw new InvalidOperationException("A file that was already there was transcribed.");
                    var firstJobs = shell.Jobs.Count;
                    if (firstJobs != 1 || shell.Jobs[0].State != TriAsr.Domain.JobState.Complete) throw new InvalidOperationException($"Expected one completed project, found {firstJobs}.");
                    if (shell.SelectedJob is not null && shell.SelectedJob.Id != shell.Jobs[0].Id) throw new InvalidOperationException("The selection moved.");
                    // A second recording as subtitles, with a text file dropped in between that must be ignored.
                    shell.WatchOutput = ShellViewModel.WatchAsSubtitles;
                    File.WriteAllText(Path.Combine(watched, "notes.txt"), "not a recording");
                    File.Copy(sample, Path.Combine(watched, "second.wav"));
                    if (!await AppearsAsync("second.srt", 300)) throw new InvalidOperationException("No subtitles appeared for the second recording. Status: " + shell.WatchStatus);
                    if (shell.Jobs.Count != 2) throw new InvalidOperationException($"Expected two projects, found {shell.Jobs.Count}.");
                    // Switched off: a recording added now waits; switching on again picks it up without transcribing anything twice.
                    shell.WatchEnabled = false;
                    for (var wait = 0; wait < 100 && shell.WatchStatus != "Off"; wait++) await Task.Delay(100);
                    File.Copy(sample, Path.Combine(watched, "while-off.wav"));
                    await Task.Delay(8000);
                    if (File.Exists(Path.Combine(watched, "while-off.srt"))) throw new InvalidOperationException("A recording was transcribed while watching was off.");
                    shell.WatchEnabled = true;
                    if (!await AppearsAsync("while-off.srt", 300)) throw new InvalidOperationException("The recording added while watching was off was not picked up. Status: " + shell.WatchStatus);
                    await Task.Delay(6000);
                    if (shell.Jobs.Count != 3) throw new InvalidOperationException($"Expected three projects, found {shell.Jobs.Count}: something was transcribed twice or missed.");
                    window.Width = 1220;
                    shell.SelectedPage = shell.Navigation[0];
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    window.ContentScroll.ScrollToVerticalOffset(600);
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    ShellSmoke.Capture(window, Path.Combine(dataRoot, "renders", "watch-folder.png"), 1220, 1100, 1);
                    await File.WriteAllTextAsync(Path.Combine(dataRoot, "watch-smoke.json"), JsonSerializer.Serialize(new
                    { copySeconds, transcript = text.Trim(), files = Directory.GetFiles(watched).Select(Path.GetFileName).Order(), projects = shell.Jobs.Count, status = shell.WatchStatus }, new JsonSerializerOptions { WriteIndented = true }));
                }
                if (e.Args.Contains("--first-run-download-smoke"))
                {
                    // Needs TRIASR_MODEL_ROOT to point at an empty folder: starts the real download, watches the banner follow it, cancels, and checks the partial file is kept.
                    var modelRoot = Environment.GetEnvironmentVariable("TRIASR_MODEL_ROOT");
                    if (string.IsNullOrEmpty(modelRoot)) throw new InvalidOperationException("Set TRIASR_MODEL_ROOT to an empty folder first.");
                    if (shell.SetupPhase != SetupStage.Offer || !shell.SetupDetail.Contains("Downloads the speech models") || !shell.SetupDetail.Contains("GiB"))
                        throw new InvalidOperationException($"With no models the banner should offer a download, but says: {shell.SetupPhase} · {shell.SetupDetail}");
                    shell.SelectedPage = shell.Navigation[0];
                    window.Width = 1220;
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    ShellSmoke.Capture(window, Path.Combine(dataRoot, "renders", "setup-offer-download.png"), 1220, 400, 1);
                    var running = shell.RunSetupCommand.ExecuteAsync(null);
                    var deadline = DateTime.UtcNow.AddSeconds(180);
                    while (DateTime.UtcNow < deadline && !(shell.SetupTitle.Contains("Downloading") && shell.SetupPercent > 0.5)) await Task.Delay(250);
                    if (!shell.SetupTitle.Contains("Downloading") || shell.SetupPercent <= 0.5) throw new InvalidOperationException($"The download did not start: {shell.SetupTitle} · {shell.SetupDetail} · {shell.ErrorMessage}");
                    await Task.Delay(1500);
                    var seen = new { shell.SetupTitle, shell.SetupDetail, shell.SetupPercent, shell.SetupIndeterminate };
                    ShellSmoke.Capture(window, Path.Combine(dataRoot, "renders", "setup-downloading.png"), 1220, 400, 1);
                    shell.CancelSetupCommand.Execute(null);
                    await running;
                    if (shell.SetupPhase != SetupStage.Problem || !shell.SetupTitle.Contains("paused")) throw new InvalidOperationException($"Cancelling should pause setup, but it is {shell.SetupPhase}: {shell.SetupTitle}");
                    var partialBytes = Directory.EnumerateFiles(modelRoot, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
                    if (partialBytes <= 0) throw new InvalidOperationException("The partial download was not kept.");
                    ShellSmoke.Capture(window, Path.Combine(dataRoot, "renders", "setup-paused.png"), 1220, 400, 1);
                    await File.WriteAllTextAsync(Path.Combine(dataRoot, "first-run-download-smoke.json"), JsonSerializer.Serialize(new
                    { seen, partialMiB = partialBytes / 1048576d, paused = shell.SetupTitle, startLabel = shell.SetupStartLabel, shell.SetupDetail }, new JsonSerializerOptions { WriteIndented = true }));
                }
                if (e.Args.Contains("--first-run-smoke"))
                {
                    // Models come from the machine's model repository (a smoke test never downloads gigabytes); the data root is fresh, so setup is pending.
                    if (shell.SetupPhase != SetupStage.Offer) throw new InvalidOperationException($"A fresh data root should be offered setup, but the banner is {shell.SetupPhase}.");
                    shell.SelectedPage = shell.Navigation[0];
                    window.Width = 1220;
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    ShellSmoke.Capture(window, Path.Combine(dataRoot, "renders", "setup-offer.png"), 1220, 760, 1);
                    var steps = new List<string>();
                    shell.PropertyChanged += (_, change) =>
                    {
                        if (change.PropertyName != nameof(ShellViewModel.SetupTitle) || steps.LastOrDefault() == shell.SetupTitle) return;
                        steps.Add(shell.SetupTitle); logger.LogInformation("Setup: {Step}", shell.SetupTitle);
                        if (shell.SetupTitle.Contains("Measuring")) ShellSmoke.Capture(window, Path.Combine(dataRoot, "renders", "setup-running.png"), 1220, 760, 1);
                    };
                    await shell.RunSetupCommand.ExecuteAsync(null);
                    if (shell.SetupPhase != SetupStage.Done) throw new InvalidOperationException($"Setup ended as {shell.SetupPhase}: {shell.SetupTitle} · {shell.SetupDetail} · {shell.ErrorMessage}");
                    var profilePath = Path.Combine(dataRoot, "Config", "tuning-results.json");
                    var activePath = Path.Combine(dataRoot, "Config", "active-execution.json");
                    if (!File.Exists(profilePath) || !File.Exists(activePath)) throw new InvalidOperationException("Setup did not save measured and applied speed settings.");
                    var profile = JsonSerializer.Deserialize<TriAsr.Benchmark.ExecutionProfile>(await File.ReadAllTextAsync(profilePath))!;
                    if (!profile.Results.Any(row => row.Engine == "Whisper" && row.Error is null) || !profile.Results.Any(row => row.Engine == "Canary" && row.Error is null))
                        throw new InvalidOperationException("Setup tuning has no measured Whisper and Canary row.");
                    if (!shell.BenchmarkSummary.StartsWith("Active")) throw new InvalidOperationException("Setup did not apply the measured settings: " + shell.BenchmarkSummary);
                    await Task.Delay(500);
                    var saved = await _host.Services.GetRequiredService<SettingsStore>().LoadAsync();
                    if (saved.SetupState != SetupPlan.Done) throw new InvalidOperationException("Setup completion was not saved.");
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    ShellSmoke.Capture(window, Path.Combine(dataRoot, "renders", "setup-done.png"), 1220, 760, 1);
                    shell.DismissSetupCommand.Execute(null);
                    if (shell.SetupBannerVisible) throw new InvalidOperationException("The finished banner did not close.");
                    var testSpeech = Path.Combine(dataRoot, "Setup", "test-speech.wav");
                    await File.WriteAllTextAsync(Path.Combine(dataRoot, "first-run-smoke.json"), JsonSerializer.Serialize(new
                    { steps, testSpeechSeconds = TriAsr.Audio.WaveAudio.Inspect(testSpeech).DurationSeconds, profile.WhisperBackend, profile.WhisperThreads, profile.CanaryBackend, profile.CanaryThreads, profile.CorrectionBackend, profile.ParallelSpeech, settled = shell.SetupDetail },
                        new JsonSerializerOptions { WriteIndented = true }));
                }
                if (e.Args.Contains("--benchmark-smoke"))
                {
                    shell.SourcePath = e.Args.Last();
                    shell.SelectedPage = shell.Navigation.First(page => page.Name == "Benchmark");
                    shell.PropertyChanged += (_, change) => { if (change.PropertyName == nameof(ShellViewModel.BenchmarkProgress)) logger.LogInformation("Benchmark: {Candidate}", shell.BenchmarkProgress); };
                    await shell.OptimizeCommand.ExecuteAsync(null);
                    var profilePath = Path.Combine(dataRoot, "Config", "tuning-results.json");
                    if (!File.Exists(profilePath)) throw new InvalidOperationException(shell.ErrorMessage);
                    var profile = JsonSerializer.Deserialize<TriAsr.Benchmark.ExecutionProfile>(await File.ReadAllTextAsync(profilePath))!;
                    if (profile.Results.Any(row => row.Error is null && row.Runs.Count != 3)) throw new InvalidOperationException("Benchmark missing measured runs.");
                    if (!shell.BenchmarkSummary.StartsWith("Best settings ready") || shell.BenchmarkResults.Count != profile.Results.Count || File.Exists(Path.Combine(dataRoot, "Config", "active-execution.json")))
                        throw new InvalidOperationException("Tuning applied settings before the user selected Apply.");
                    await shell.ApplyBestSettingsCommand.ExecuteAsync(null);
                    if (!shell.BenchmarkSummary.StartsWith("Active"))
                        throw new InvalidOperationException("Tuning results did not reach the UI or were not applied.");
                    using (var reloaded = CreateHost(dataRoot))
                    {
                        var savedShell = reloaded.Services.GetRequiredService<ShellViewModel>();
                        await savedShell.InitializeAsync(); await savedShell.DetectHardwareCommand.ExecuteAsync(null);
                        if (savedShell.WhisperBackendChoice != profile.WhisperBackend || savedShell.CanaryBackendChoice != profile.CanaryBackend || savedShell.CorrectionBackendChoice != profile.CorrectionBackend
                            || savedShell.WhisperThreadChoice != profile.WhisperThreads || savedShell.CanaryThreadChoice != profile.CanaryThreads || savedShell.CorrectionThreadChoice != profile.CorrectionThreads || savedShell.ParallelChoice != profile.ParallelSpeech)
                            throw new InvalidOperationException("Applied best execution values did not survive reload.");
                    }
                    window.Width = 1220;
                    window.ContentScroll.ScrollToVerticalOffset(900);
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    ShellSmoke.Capture(window, Path.Combine(dataRoot, "renders", "benchmark-results.png"), 1220, 1000, 1);
                    await File.WriteAllTextAsync(Path.Combine(dataRoot, "benchmark-smoke.json"), JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
                }
                if (e.Args.Contains("--update-smoke"))
                {
                    if (!shell.CanSelfUpdate) throw new InvalidOperationException("Update smoke needs a loopback source in TRIASR_UPDATE_MANIFEST.");
                    await shell.RunUpdateCheckAsync(manual: true);
                    if (shell.AvailableUpdate is null || !shell.UpdateBannerVisible)
                        throw new InvalidOperationException("The update banner did not appear for a newer release: " + shell.UpdateStatusText);
                    foreach (var theme in new[] { "Light", "Dark" })
                    {
                        shell.SelectedTheme = theme; window.Width = 1220;
                        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        ShellSmoke.Capture(window, Path.Combine(dataRoot, "renders", $"update-banner-{theme}.png"), 1220, 700, 1);
                    }
                    shell.SelectedPage = shell.Navigation.First(page => page.Name == "Settings");
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    window.ContentScroll.ScrollToEnd();
                    await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    ShellSmoke.Capture(window, Path.Combine(dataRoot, "renders", "update-settings.png"), 1220, 1100, 1);
                    shell.SelectedPage = shell.Navigation[0];
                    await File.WriteAllTextAsync(Path.Combine(dataRoot, "update-smoke.json"),
                        JsonSerializer.Serialize(new { offered = shell.AvailableUpdate.VersionText, bannerShown = true, sha256 = shell.AvailableUpdate.Sha256 }));
                    if (e.Args.Contains("--update-install"))
                    {
                        shell.UpdateRelaunchArguments = "--smoke-test";
                        await shell.InstallUpdateCommand.ExecuteAsync(null);
                        if (shell.HasError) throw new InvalidOperationException(shell.ErrorTitle + ": " + shell.ErrorMessage);
                        await StopHostAsync();
                        Shutdown(0);
                        return;
                    }
                }
                await using var connection = await _host.Services.GetRequiredService<SqliteConnectionFactory>().OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT sqlite_version();";
                var version = (string?)await command.ExecuteScalarAsync();
                await File.WriteAllTextAsync(Path.Combine(dataRoot, "bootstrap-smoke.json"),
                    JsonSerializer.Serialize(new { success = true, windowCreated = true, sqliteVersion = version,
                        shellMatrix = matrix, architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                        utc = DateTimeOffset.UtcNow }));
                logger.LogInformation("Shell smoke passed: {RenderCount} renders", matrix);
                window.Close();
                await StopHostAsync();
                Shutdown(0);
                return;
            }
            window.Show();
            _ = shell.RunUpdateCheckAsync(manual: false);
        }
        catch (Exception exception)
        {
            _host?.Services.GetService<ILogger<App>>()?.LogCritical(exception, "Application startup failed");
            if (!smoke) MessageBox.Show(exception.Message, "Mockingbird Studio could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            await StopHostAsync();
            Shutdown(1);
        }
    }

    private async Task StopHostAsync()
    {
        var host = _host;
        _host = null;
        if (host is null) return;
        try { await host.StopAsync(TimeSpan.FromSeconds(5)); }
        finally { host.Dispose(); }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _host = null;
        base.OnExit(e);
    }
}
