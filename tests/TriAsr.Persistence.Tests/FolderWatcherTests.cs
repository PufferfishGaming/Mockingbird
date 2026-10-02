using TriAsr.Infrastructure;

namespace TriAsr.Persistence.Tests;

public sealed class FolderWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", "watch-" + Guid.NewGuid().ToString("N"));
    private string Folder => Path.Combine(_root, "in");
    private string LedgerPath => Path.Combine(_root, "ledger.json");
    private static readonly WatchOptions Quick = new(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(400));

    public FolderWatcherTests() => Directory.CreateDirectory(Folder);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private static async Task WaitUntilAsync(Func<bool> condition, string because, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline) { if (condition()) return; await Task.Delay(25); }
        throw new TimeoutException("Not reached: " + because);
    }

    private string Write(string name, int bytes = 2048)
    {
        var path = Path.Combine(Folder, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Theory]
    [InlineData("talk.wav", true)]
    [InlineData("TALK.MP3", true)]
    [InlineData("clip.M4a", true)]
    [InlineData("film.mkv", true)]
    [InlineData("notes.txt", false)]
    [InlineData("talk.wav.part", false)]
    [InlineData("video.mp4.crdownload", false)]
    [InlineData(".hidden.wav", false)]
    [InlineData("~$talk.wav", false)]
    [InlineData("noextension", false)]
    public void OnlyAudioAndVideoAreWatchedFor(string name, bool expected) => Assert.Equal(expected, WatchRules.IsMedia(Path.Combine(Folder, name)));

    [Fact]
    public void AReplacedFileIsANewRecording()
    {
        var path = Write("a.wav", 100);
        var first = WatchRules.Key(new FileInfo(path));
        File.WriteAllBytes(path, new byte[200]);
        Assert.NotEqual(first, WatchRules.Key(new FileInfo(path)));
        Assert.Equal(WatchRules.Key(new FileInfo(path)), WatchRules.Key(new FileInfo(path.ToUpperInvariant())));
    }

    [Fact]
    public void AnExportNeverReplacesAFile()
    {
        var path = Path.Combine(Folder, "talk.txt");
        Assert.Equal(path, WatchRules.UniqueSibling(path));
        File.WriteAllText(path, "mine");
        var second = WatchRules.UniqueSibling(path);
        Assert.Equal(Path.Combine(Folder, "talk (2).txt"), second);
        File.WriteAllText(second, "also mine");
        Assert.Equal(Path.Combine(Folder, "talk (3).txt"), WatchRules.UniqueSibling(path));
    }

    [Fact]
    public async Task TheFirstTimeAFolderIsWatchedWhatIsAlreadyThereIsNotTranscribed()
    {
        Write("old.wav");
        var ledger = await WatchLedger.LoadAsync(LedgerPath);
        await ledger.EnsureBaselineAsync(Folder);
        Assert.True(ledger.Contains(WatchRules.Key(new FileInfo(Path.Combine(Folder, "old.wav")))));
        var again = await WatchLedger.LoadAsync(LedgerPath); // survives a restart
        Assert.Equal(Path.GetFullPath(Folder), again.Folder);
        Write("added-while-closed.wav");
        await again.EnsureBaselineAsync(Folder); // the same folder keeps its record, so this one is still to do
        Assert.False(again.Contains(WatchRules.Key(new FileInfo(Path.Combine(Folder, "added-while-closed.wav")))));
        var other = Path.Combine(_root, "other"); Directory.CreateDirectory(other);
        await again.EnsureBaselineAsync(other);
        Assert.Equal(Path.GetFullPath(other), again.Folder);
        Assert.False(again.Contains(WatchRules.Key(new FileInfo(Path.Combine(Folder, "old.wav"))))); // another folder starts a new record
    }

    [Fact]
    public async Task ANewRecordingIsHandledOnceItHasFinishedArriving()
    {
        var ledger = await WatchLedger.LoadAsync(LedgerPath);
        await ledger.EnsureBaselineAsync(Folder);
        var seen = new List<(string Name, long Length)>();
        await using var watcher = new FolderWatcher(Folder, ledger, (path, _) => { lock (seen) seen.Add((Path.GetFileName(path), new FileInfo(path).Length)); return Task.FromResult(WatchOutcome.Done); }, Quick);
        watcher.Start();

        var path = Path.Combine(Folder, "meeting.wav");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            for (var chunk = 0; chunk < 8; chunk++) { stream.Write(new byte[1000]); stream.Flush(); await Task.Delay(120); }
            lock (seen) Assert.Empty(seen); // still being written: not handed over yet
        }
        await WaitUntilAsync(() => { lock (seen) return seen.Count > 0; }, "the finished recording is handled");
        await Task.Delay(1000); // and it is not handled a second time
        lock (seen) Assert.Equal([("meeting.wav", 8000L)], seen);
    }

    [Fact]
    public async Task AFileHeldOpenForWritingWaitsEvenWhenItsSizeStopsChanging()
    {
        var ledger = await WatchLedger.LoadAsync(LedgerPath);
        await ledger.EnsureBaselineAsync(Folder);
        var count = 0;
        await using var watcher = new FolderWatcher(Folder, ledger, (_, _) => { Interlocked.Increment(ref count); return Task.FromResult(WatchOutcome.Done); }, Quick);
        watcher.Start();
        var path = Path.Combine(Folder, "held.mp4");
        using (var held = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            held.Write(new byte[500]); held.Flush();
            await Task.Delay(1500);
            Assert.Equal(0, Volatile.Read(ref count));
        }
        await WaitUntilAsync(() => Volatile.Read(ref count) == 1, "handled after the writer let go");
    }

    [Fact]
    public async Task TextFilesAreIgnoredAndRecordingsAreHandledOneAtATime()
    {
        var ledger = await WatchLedger.LoadAsync(LedgerPath);
        await ledger.EnsureBaselineAsync(Folder);
        var running = 0; var overlapped = false; var done = new List<string>();
        await using var watcher = new FolderWatcher(Folder, ledger, async (path, token) =>
        {
            if (Interlocked.Increment(ref running) > 1) overlapped = true;
            await Task.Delay(300, token);
            lock (done) done.Add(Path.GetFileName(path));
            Interlocked.Decrement(ref running);
            return WatchOutcome.Done;
        }, Quick);
        watcher.Start();
        Write("one.wav"); Write("two.mp3"); Write("notes.txt"); Write("three.flac");
        await WaitUntilAsync(() => { lock (done) return done.Count == 3; }, "all three recordings are handled");
        Assert.False(overlapped);
        lock (done) Assert.DoesNotContain("notes.txt", done);
        await WaitUntilAsync(() => watcher.Waiting == 0, "nothing is left waiting");
    }

    [Fact]
    public async Task ARecordingThatFailsIsNotRetriedAndDoesNotStopTheWatching()
    {
        var ledger = await WatchLedger.LoadAsync(LedgerPath);
        await ledger.EnsureBaselineAsync(Folder);
        var attempts = new List<string>();
        await using var watcher = new FolderWatcher(Folder, ledger, (path, _) =>
        {
            lock (attempts) attempts.Add(Path.GetFileName(path));
            if (path.EndsWith("broken.wav")) throw new InvalidOperationException("cannot read");
            return Task.FromResult(WatchOutcome.Done);
        }, Quick);
        watcher.Start();
        Write("broken.wav", 111); Write("fine.wav", 222);
        await WaitUntilAsync(() => { lock (attempts) return attempts.Contains("fine.wav"); }, "the file after the broken one is handled");
        await Task.Delay(1200); // several rescans
        lock (attempts) Assert.Equal(1, attempts.Count(name => name == "broken.wav"));
    }

    [Fact]
    public async Task ARetryIsPickedUpAgainByTheNextWatcher()
    {
        var ledger = await WatchLedger.LoadAsync(LedgerPath);
        await ledger.EnsureBaselineAsync(Folder);
        var first = 0;
        await using (var watcher = new FolderWatcher(Folder, ledger, (_, _) => { Interlocked.Increment(ref first); return Task.FromResult(WatchOutcome.Retry); }, Quick))
        {
            watcher.Start();
            Write("later.wav");
            await WaitUntilAsync(() => Volatile.Read(ref first) >= 1, "the first watcher is offered the recording");
        }
        var second = new List<string>();
        var reloaded = await WatchLedger.LoadAsync(LedgerPath);
        await using var next = new FolderWatcher(Folder, reloaded, (path, _) => { lock (second) second.Add(Path.GetFileName(path)); return Task.FromResult(WatchOutcome.Done); }, Quick);
        await reloaded.EnsureBaselineAsync(Folder);
        next.Start();
        await WaitUntilAsync(() => { lock (second) return second.Contains("later.wav"); }, "the next watcher gets it again");
    }

    [Fact]
    public async Task StoppingWhileARecordingIsBeingHandledLeavesItForNextTime()
    {
        var ledger = await WatchLedger.LoadAsync(LedgerPath);
        await ledger.EnsureBaselineAsync(Folder);
        var started = new TaskCompletionSource();
        var watcher = new FolderWatcher(Folder, ledger, async (_, token) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return WatchOutcome.Done; }, Quick);
        watcher.Start();
        var path = Write("long.wav");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await watcher.DisposeAsync(); // cancels the handler
        Assert.False((await WatchLedger.LoadAsync(LedgerPath)).Contains(WatchRules.Key(new FileInfo(path))));
    }
}
