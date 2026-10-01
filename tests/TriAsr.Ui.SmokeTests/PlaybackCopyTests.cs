using System.IO;
using TriAsr.Application;
using TriAsr.Audio;
using TriAsr.Domain;
using TriAsr.Infrastructure;

namespace TriAsr.Ui.SmokeTests;

public sealed class PlaybackCopyTests
{
    // ---- the queue: the listening copy is optional and must never change the outcome of a job ----

    private sealed class Workspace(string root) : IJobWorkspace
    {
        public string DirectoryFor(Guid jobId) { var path = Path.Combine(root, jobId.ToString("N")); Directory.CreateDirectory(path); return path; }
        public Task CreateAsync(TranscriptionJob job, CancellationToken token) { DirectoryFor(job.Id); return Task.CompletedTask; }
    }
    private sealed class Repository : IJobRepository
    {
        public List<TranscriptionJob> Saved { get; } = [];
        public Task InitializeAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task SaveAsync(TranscriptionJob job, CancellationToken token = default) { lock (Saved) Saved.Add(job); return Task.CompletedTask; }
        public Task<IReadOnlyList<TranscriptionJob>> ListAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<TranscriptionJob>>(Saved.ToArray());
    }
    private sealed class FakeAudio(Func<string, string, Task>? normalize = null, Func<string, string, CancellationToken, Task>? playback = null) : IAudioNormalizer
    {
        public List<string> PlaybackDestinations { get; } = [];
        public Task<AudioInfo> NormalizeAsync(string source, string destination, CancellationToken token) =>
            (normalize?.Invoke(source, destination) ?? Task.CompletedTask).ContinueWith(_ => new AudioInfo(16000, 1, 16, 16000), token);
        public Task CreatePlaybackCopyAsync(string source, string destination, CancellationToken token)
        {
            lock (PlaybackDestinations) PlaybackDestinations.Add(destination);
            return playback?.Invoke(source, destination, token) ?? Task.CompletedTask;
        }
    }
    /// <summary>An implementer written before the listening copy existed: it relies on the default no-op.</summary>
    private sealed class LegacyAudio : IAudioNormalizer
    {
        public Task<AudioInfo> NormalizeAsync(string source, string destination, CancellationToken token) => Task.FromResult(new AudioInfo(16000, 1, 16, 16000));
    }

    private static async Task<(TranscriptionJob Job, string Directory)> Run(IAudioNormalizer audio, CancellationToken token = default)
    {
        var root = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
        var queue = new AudioJobQueue(new Repository(), new Workspace(root), audio);
        var job = await queue.EnqueueAsync("source.m4a", "auto");
        var prepared = await queue.PrepareAsync(job, token);
        return (prepared, new Workspace(root).DirectoryFor(job.Id));
    }

    [Fact]
    public async Task TheListeningCopyIsRequestedNextToTheNormalizedFileAndTheJobProceeds()
    {
        var audio = new FakeAudio();
        var (job, directory) = await Run(audio);
        Assert.Equal(JobState.Queued, job.State);
        Assert.Equal("normalized", job.Checkpoint);
        Assert.Equal(Path.Combine(directory, "playback.m4a"), Assert.Single(audio.PlaybackDestinations));
    }

    [Fact]
    public async Task AFailingListeningCopyNeverFailsTheJob()
    {
        var (job, _) = await Run(new FakeAudio(playback: (_, _, _) => throw new InvalidOperationException("encoder missing")));
        Assert.Equal(JobState.Queued, job.State);
        Assert.Null(job.Error);
    }

    [Fact]
    public async Task ASlowListeningCopyIsAwaitedSoNoWorkIsLeftRunningInTheBackground()
    {
        var finished = false;
        var (job, _) = await Run(new FakeAudio(playback: async (_, _, _) => { await Task.Delay(300); finished = true; }));
        Assert.Equal(JobState.Queued, job.State);
        Assert.True(finished);
    }

    [Fact]
    public async Task AFailedNormalizationStillFailsTheJobAndStillWaitsForTheCopy()
    {
        var finished = false;
        var (job, _) = await Run(new FakeAudio(normalize: (_, _) => throw new InvalidOperationException("bad media"),
            playback: async (_, _, _) => { await Task.Delay(200); finished = true; }));
        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("bad media", job.Error);
        Assert.True(finished);
    }

    [Fact]
    public async Task CancellingDuringTheCopyCancelsTheJobRatherThanFailingIt()
    {
        using var cancellation = new CancellationTokenSource();
        var (job, _) = await Run(new FakeAudio(playback: async (_, _, token) => { cancellation.Cancel(); await Task.Delay(Timeout.Infinite, token); }), cancellation.Token);
        Assert.Equal(JobState.Cancelled, job.State);
    }

    [Fact]
    public async Task ImplementationsWrittenBeforeTheCopyExistedKeepWorking()
    {
        var (job, _) = await Run(new LegacyAudio());
        Assert.Equal(JobState.Queued, job.State);
    }

    // ---- the encoder arguments ----

    [Fact]
    public void TheEncoderMakesHighBitrate48KilohertzAacAndKeepsMonoAndStereo()
    {
        var arguments = FfmpegNormalizer.PlaybackArguments("in.mp4", "out.m4a");
        Assert.Equal(["-threads", "2"], arguments.SkipWhile(a => a != "-threads").Take(2));
        Assert.Contains("aac", arguments); Assert.Contains("192k", arguments); Assert.Contains("48000", arguments);
        Assert.Contains("aformat=channel_layouts=mono|stereo", arguments);
        Assert.Equal("out.m4a", arguments[^1]);
        Assert.True(Array.IndexOf(arguments, "-i") < Array.IndexOf(arguments, "-af"), "input options must come before the filter");
        Assert.DoesNotContain("-y", arguments); // never overwrite
    }

    // ---- the real FFmpeg, when it is installed on this machine ----

    private sealed class FfmpegFactAttribute : FactAttribute
    {
        public FfmpegFactAttribute() { if (FindFfmpeg() is null) Skip = "No FFmpeg runtime found: pending on a machine with Runtimes/FFmpeg/ffmpeg.exe."; }
    }
    private static string? FindFfmpeg()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("TRIASR_RUNTIME_ROOT") is { Length: > 0 } runtime) candidates.Add(Path.Combine(runtime, "Runtimes", "FFmpeg", "ffmpeg.exe"));
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "TriAsr.slnx"))) { candidates.Add(Path.Combine(directory.FullName, "Runtimes", "FFmpeg", "ffmpeg.exe")); break; }
        return candidates.FirstOrDefault(File.Exists);
    }
    private static async Task<ProcessResult> Ffmpeg(string ffmpeg, params string[] arguments) =>
        await new ProcessRunner().RunAsync(new(ffmpeg, arguments, Path.GetTempPath(), TimeSpan.FromMinutes(1)));

    [FfmpegFact]
    public async Task TheRealEncoderProducesA48KilohertzStereoCopyBesideTheSpeechFile()
    {
        var ffmpeg = FindFfmpeg()!;
        var root = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.wav");
            Assert.Equal(0, (await Ffmpeg(ffmpeg, "-nostdin", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=2", "-ac", "2", source)).ExitCode);
            var normalizer = new FfmpegNormalizer(new ProcessRunner(), ffmpeg);
            var normalized = Path.Combine(root, "normalized.wav"); var playback = Path.Combine(root, "playback.m4a");
            var info = await normalizer.NormalizeAsync(source, normalized, CancellationToken.None);
            await normalizer.CreatePlaybackCopyAsync(source, playback, CancellationToken.None);
            Assert.Equal((16000, 1), (info.SampleRate, info.Channels));
            var probe = (await Ffmpeg(ffmpeg, "-hide_banner", "-i", playback)).StandardError;
            Assert.Contains("Audio: aac", probe); Assert.Contains("48000 Hz", probe); Assert.Contains("stereo", probe);
            // A second request must not re-encode, and no temporary files may remain.
            var written = File.GetLastWriteTimeUtc(playback);
            await normalizer.CreatePlaybackCopyAsync(source, playback, CancellationToken.None);
            Assert.Equal(written, File.GetLastWriteTimeUtc(playback));
            Assert.Equal(["normalized.wav", "playback.m4a", "source.wav"], Directory.GetFiles(root).Select(Path.GetFileName).Order());
        }
        finally { TestCleanup.Delete(root); }
    }

    [FfmpegFact]
    public async Task AMonoSourceStaysMonoAndAMissingSourceIsIgnored()
    {
        var ffmpeg = FindFfmpeg()!;
        var root = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "mono.wav");
            Assert.Equal(0, (await Ffmpeg(ffmpeg, "-nostdin", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=330:sample_rate=22050:duration=1", "-ac", "1", source)).ExitCode);
            var normalizer = new FfmpegNormalizer(new ProcessRunner(), ffmpeg);
            var playback = Path.Combine(root, "playback.m4a");
            await normalizer.CreatePlaybackCopyAsync(source, playback, CancellationToken.None);
            var probe = (await Ffmpeg(ffmpeg, "-hide_banner", "-i", playback)).StandardError;
            Assert.Contains("48000 Hz", probe); Assert.Contains("mono", probe);
            var none = Path.Combine(root, "none.m4a");
            await normalizer.CreatePlaybackCopyAsync(Path.Combine(root, "missing.wav"), none, CancellationToken.None);
            Assert.False(File.Exists(none));
        }
        finally { TestCleanup.Delete(root); }
    }

    [FfmpegFact]
    public async Task AFileThatIsNotAudioLeavesNoCopyAndNoDebris()
    {
        var ffmpeg = FindFfmpeg()!;
        var root = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "notes.txt"); await File.WriteAllTextAsync(source, "this is not audio");
            await new FfmpegNormalizer(new ProcessRunner(), ffmpeg).CreatePlaybackCopyAsync(source, Path.Combine(root, "playback.m4a"), CancellationToken.None);
            Assert.Equal(["notes.txt"], Directory.GetFiles(root).Select(Path.GetFileName));
        }
        finally { TestCleanup.Delete(root); }
    }
}
