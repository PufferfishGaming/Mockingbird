using System.Text.Json;
using System.Threading.Channels;

namespace TriAsr.Infrastructure;

/// <summary>What the watch-folder handler did with a recording.</summary>
public enum WatchOutcome
{
    /// <summary>Transcribed; remember it so it is not transcribed again.</summary>
    Done,
    /// <summary>Tried and failed; remember it so a broken file is not retried forever.</summary>
    Failed,
    /// <summary>Could not be tried now (stopped, or something it needs is missing); it is picked up again next time.</summary>
    Retry
}

public sealed record WatchOptions(TimeSpan StableFor, TimeSpan PollInterval, TimeSpan RescanInterval)
{
    /// <summary>A recording counts as complete once its size and time have not changed for four seconds and nobody is still writing it.</summary>
    public static WatchOptions Default { get; } = new(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
}

/// <summary>Which files a watched folder reacts to, and how a recording is told apart from an earlier copy of the same name.</summary>
public static class WatchRules
{
    public static readonly IReadOnlyList<string> MediaExtensions = [".wav", ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".mp4", ".mkv", ".mov", ".webm"];

    /// <summary>Audio or video the app can read. Hidden and temporary names are left alone, and so are in-progress downloads (their extension is .part or .crdownload).</summary>
    public static bool IsMedia(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Length == 0 || name.StartsWith('.') || name.StartsWith("~$", StringComparison.Ordinal)) return false;
        return MediaExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Path, size and last write time: a file that is replaced under the same name is a new recording.</summary>
    public static string Key(FileInfo file) => $"{file.FullName.ToLowerInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";

    public static IEnumerable<FileInfo> MediaIn(string folder) => Directory.EnumerateFiles(folder).Where(IsMedia).Select(path => new FileInfo(path));

    /// <summary>The path itself if it is free, otherwise "name (2).ext", "name (3).ext"... so an export never replaces a user's file.</summary>
    public static string UniqueSibling(string path)
    {
        if (!File.Exists(path)) return path;
        var folder = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var number = 2; ; number++)
        {
            var candidate = Path.Combine(folder, $"{name} ({number}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}

/// <summary>
/// The recordings of one folder that were already dealt with, kept on disk so a restart neither repeats them nor skips what arrived meanwhile.
/// The first time a folder is watched, what is already in it is recorded without being transcribed: only recordings added afterwards are.
/// </summary>
public sealed class WatchLedger
{
    private sealed record State(string? Folder, string[] Keys);
    private readonly string _path;
    private readonly HashSet<string> _keys;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string? Folder { get; private set; }

    private WatchLedger(string path, string? folder, IEnumerable<string> keys) { _path = path; Folder = folder; _keys = new(keys, StringComparer.Ordinal); }

    public static async Task<WatchLedger> LoadAsync(string path, CancellationToken token = default)
    {
        try
        {
            if (File.Exists(path))
            {
                var state = JsonSerializer.Deserialize<State>(await File.ReadAllTextAsync(path, token));
                if (state is not null) return new(path, state.Folder, state.Keys ?? []);
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
        return new(path, null, []);
    }

    public bool Contains(string key) { lock (_keys) return _keys.Contains(key); }

    public async Task AddAsync(string key, CancellationToken token = default)
    {
        lock (_keys) _keys.Add(key);
        await SaveAsync(token);
    }

    /// <summary>Starts a fresh record for <paramref name="folder"/> unless this ledger already belongs to it: what the folder holds right now counts as seen.</summary>
    public async Task EnsureBaselineAsync(string folder, CancellationToken token = default)
    {
        var full = Path.GetFullPath(folder);
        if (string.Equals(Folder, full, StringComparison.OrdinalIgnoreCase)) return;
        lock (_keys) { _keys.Clear(); foreach (var file in WatchRules.MediaIn(full)) _keys.Add(WatchRules.Key(file)); Folder = full; }
        await SaveAsync(token);
    }

    private async Task SaveAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            string[] keys; string? folder;
            lock (_keys) { keys = [.. _keys]; folder = Folder; }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new State(folder, keys)), token);
            File.Move(temporary, _path, true);
        }
        finally { _gate.Release(); }
    }
}

/// <summary>
/// Watches the top level of one folder and hands each new recording to <c>handle</c>, one at a time, once it has finished arriving.
/// A copy in progress is waited for (size and time stable, not open for writing). A slow rescan backs up the file system events,
/// which are unreliable on network shares.
/// </summary>
public sealed class FolderWatcher : IAsyncDisposable
{
    private sealed class Sighting { public long Length = -1; public long WriteTicks; public DateTime Since = DateTime.UtcNow; }

    private readonly string _folder;
    private readonly WatchLedger _ledger;
    private readonly Func<string, CancellationToken, Task<WatchOutcome>> _handle;
    private readonly WatchOptions _options;
    private readonly Dictionary<string, Sighting> _arriving = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _queued = new(StringComparer.Ordinal);
    private readonly Channel<FileInfo> _ready = Channel.CreateUnbounded<FileInfo>(new() { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private FileSystemWatcher? _events;
    private Task? _polling, _processing;

    /// <summary>Raised when the count of recordings that are arriving or waiting for their turn changes (on a background thread).</summary>
    public event Action? Changed;
    public string Folder => _folder;
    public int Waiting { get { lock (_arriving) return _arriving.Count + _queued.Count; } }

    public FolderWatcher(string folder, WatchLedger ledger, Func<string, CancellationToken, Task<WatchOutcome>> handle, WatchOptions? options = null)
    {
        _folder = Path.GetFullPath(folder); _ledger = ledger; _handle = handle; _options = options ?? WatchOptions.Default;
    }

    public void Start()
    {
        if (_polling is not null) throw new InvalidOperationException("The watcher is already running.");
        TryStartEvents();
        Rescan();
        _polling = Task.Run(() => PollAsync(_stop.Token));
        _processing = Task.Run(() => ProcessAsync(_stop.Token));
    }

    private void TryStartEvents()
    {
        try
        {
            _events = new FileSystemWatcher(_folder) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size, InternalBufferSize = 64 * 1024 };
            _events.Created += (_, change) => Observe(change.FullPath);
            _events.Changed += (_, change) => Observe(change.FullPath);
            _events.Renamed += (_, change) => Observe(change.FullPath);
            _events.Error += (_, _) => Rescan();
            _events.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException) { _events?.Dispose(); _events = null; } // the rescan still finds new files
    }

    private void Observe(string path)
    {
        if (_stop.IsCancellationRequested || !WatchRules.IsMedia(path)) return;
        lock (_arriving)
        {
            if (!_arriving.TryGetValue(path, out var sighting)) _arriving[path] = sighting = new();
            sighting.Since = DateTime.UtcNow; // any event means it is still changing
        }
        Changed?.Invoke();
    }

    private void Rescan()
    {
        try
        {
            foreach (var file in WatchRules.MediaIn(_folder))
            {
                if (_ledger.Contains(WatchRules.Key(file))) continue;
                lock (_arriving) { if (_queued.Contains(WatchRules.Key(file)) || _arriving.ContainsKey(file.FullName)) continue; _arriving[file.FullName] = new(); }
                Changed?.Invoke();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } // the folder is unavailable for now; the next rescan tries again
    }

    private async Task PollAsync(CancellationToken token)
    {
        var lastRescan = DateTime.UtcNow;
        using var timer = new PeriodicTimer(_options.PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (DateTime.UtcNow - lastRescan >= _options.RescanInterval) { Rescan(); lastRescan = DateTime.UtcNow; }
                string[] paths;
                lock (_arriving) paths = [.. _arriving.Keys];
                foreach (var path in paths) CheckArrival(path);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void CheckArrival(string path)
    {
        FileInfo info;
        try { info = new FileInfo(path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return; }
        lock (_arriving)
        {
            if (!_arriving.TryGetValue(path, out var sighting)) return;
            if (!info.Exists) { _arriving.Remove(path); }
            else if (info.Length != sighting.Length || info.LastWriteTimeUtc.Ticks != sighting.WriteTicks)
            { sighting.Length = info.Length; sighting.WriteTicks = info.LastWriteTimeUtc.Ticks; sighting.Since = DateTime.UtcNow; return; }
            else if (info.Length == 0 || DateTime.UtcNow - sighting.Since < _options.StableFor) return;
        }
        if (!info.Exists) { Changed?.Invoke(); return; }
        try { using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); } // fails while another program is still writing
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { lock (_arriving) { if (_arriving.TryGetValue(path, out var again)) again.Since = DateTime.UtcNow; } return; }
        var key = WatchRules.Key(info);
        bool queue;
        lock (_arriving) { _arriving.Remove(path); queue = !_ledger.Contains(key) && _queued.Add(key); }
        if (queue) _ready.Writer.TryWrite(info);
        Changed?.Invoke();
    }

    private async Task ProcessAsync(CancellationToken token)
    {
        try
        {
            await foreach (var file in _ready.Reader.ReadAllAsync(token))
            {
                var key = WatchRules.Key(file);
                try
                {
                    var current = new FileInfo(file.FullName);
                    if (!current.Exists || WatchRules.Key(current) != key) continue; // changed or removed while it waited
                    WatchOutcome outcome;
                    try { outcome = await _handle(file.FullName, token); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { outcome = WatchOutcome.Failed; } // a handler that throws must not end the watching
                    if (outcome != WatchOutcome.Retry) await _ledger.AddAsync(key, CancellationToken.None);
                }
                finally { lock (_arriving) _queued.Remove(key); Changed?.Invoke(); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _events?.Dispose();
        _ready.Writer.TryComplete();
        foreach (var task in new[] { _polling, _processing }) if (task is not null) { try { await task; } catch (OperationCanceledException) { } }
        _stop.Dispose();
    }
}
