using System.Buffers.Binary;
using System.Diagnostics;

namespace TriAsr.Audio.Recording;

/// <summary>A finished recording: where it is, how long it is, and whether anything at all was heard.</summary>
public sealed record RecordedFile(string Path, TimeSpan Duration, long Bytes, bool Silent);

/// <summary>
/// One recording from start to stop: the sound is written to a WAV file as it arrives (so a long recording is never held in memory and a crash loses
/// at most the last couple of seconds), with the size in the file's header kept up to date. The level of the last piece is available for a meter.
/// </summary>
public sealed class RecordingSession : IDisposable
{
    private const int HeaderBytes = 44;
    private const int BytesPerSecond = WindowsMicrophone.SampleRate * 2;
    private static readonly TimeSpan HeaderInterval = TimeSpan.FromSeconds(2);

    private readonly IMicrophone _microphone;
    private readonly object _gate = new();
    private FileStream? _file;
    private IDisposable? _capture;
    private long _dataBytes;
    private long _nonZeroSamples;
    private double _level;
    private Exception? _failure;
    private readonly Stopwatch _sinceHeader = new();

    public RecordingSession(IMicrophone microphone, string folder)
    {
        _microphone = microphone;
        Folder = folder;
    }

    public string Folder { get; }
    public string? Path { get; private set; }
    public bool IsRecording { get; private set; }

    /// <summary>How long has been recorded, counted from the sound that arrived (so it is the length of the file, not of the clock).</summary>
    public TimeSpan Duration { get { lock (_gate) return TimeSpan.FromSeconds((double)_dataBytes / BytesPerSecond); } }

    /// <summary>The loudest sample of the most recent piece of sound, 0 (silence) to 1 (as loud as can be recorded).</summary>
    public double Level { get { lock (_gate) return _level; } }

    /// <summary>Whether sound has arrived and every sample of it was exactly zero, which is what Windows delivers when it blocks the microphone or it is muted.</summary>
    public bool HeardNothing { get { lock (_gate) return _dataBytes >= BytesPerSecond && _nonZeroSamples == 0; } }

    /// <summary>The problem that stopped the recording by itself (the microphone was unplugged), if there was one.</summary>
    public Exception? Failure { get { lock (_gate) return _failure; } }

    /// <summary>Raised on the thread of the microphone when the recording stopped by itself. The file so far is complete and usable.</summary>
    public event Action<Exception>? Failed;

    public void Start(int deviceId)
    {
        if (IsRecording) throw new InvalidOperationException("A recording is already running.");
        Directory.CreateDirectory(Folder);
        var free = new DriveInfo(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(Folder))!).AvailableFreeSpace;
        if (free < 512L * 1024 * 1024) throw new MicrophoneException("There is not enough free disk space to record.");
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
        var path = System.IO.Path.Combine(Folder, $"Recording {stamp}.wav");
        for (var number = 2; File.Exists(path); number++) path = System.IO.Path.Combine(Folder, $"Recording {stamp} ({number}).wav");
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try
        {
            WriteHeader(file, 0);
            lock (_gate) { _file = file; _dataBytes = 0; _nonZeroSamples = 0; _level = 0; _failure = null; }
            Path = path;
            _sinceHeader.Restart();
            _capture = _microphone.Start(deviceId, OnData, OnFailure);
            IsRecording = true;
        }
        catch
        {
            lock (_gate) _file = null;
            file.Dispose();
            try { File.Delete(path); } catch (IOException) { }
            Path = null;
            throw;
        }
    }

    private void OnData(ReadOnlyMemory<byte> piece)
    {
        var span = piece.Span;
        var peak = 0;
        var nonZero = 0L;
        for (var i = 0; i + 1 < span.Length; i += 2)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(span[i..]);
            if (sample != 0) nonZero++;
            var size = sample == short.MinValue ? short.MaxValue : Math.Abs((int)sample);
            if (size > peak) peak = size;
        }
        lock (_gate)
        {
            if (_file is null) return;
            _file.Write(span);
            _dataBytes += span.Length;
            _nonZeroSamples += nonZero;
            _level = peak / (double)short.MaxValue;
            if (_sinceHeader.Elapsed >= HeaderInterval) { PatchHeader(); _sinceHeader.Restart(); }
        }
    }

    private void OnFailure(Exception error)
    {
        lock (_gate) _failure = error;
        Failed?.Invoke(error);
    }

    private void PatchHeader()
    {
        var file = _file!;
        var position = file.Position;
        file.Flush();
        file.Position = 0;
        WriteHeader(file, _dataBytes);
        file.Position = position;
        file.Flush();
    }

    /// <summary>Stops, completes the file and returns it.</summary>
    public RecordedFile Stop()
    {
        if (!IsRecording && _file is null) throw new InvalidOperationException("No recording is running.");
        IsRecording = false;
        _capture?.Dispose();   // waits for the last piece of sound
        _capture = null;
        lock (_gate)
        {
            var file = _file!;
            _file = null;
            file.Flush();
            file.Position = 0;
            WriteHeader(file, _dataBytes);
            file.Dispose();
            var silent = _dataBytes == 0 || _nonZeroSamples == 0;
            return new RecordedFile(Path!, TimeSpan.FromSeconds((double)_dataBytes / BytesPerSecond), _dataBytes + HeaderBytes, silent);
        }
    }

    /// <summary>Stops and deletes what was recorded.</summary>
    public void Discard()
    {
        if (!IsRecording && _file is null) return;
        var recorded = Stop();
        try { File.Delete(recorded.Path); } catch (IOException) { }
    }

    public void Dispose()
    {
        if (IsRecording || _file is not null) { try { Stop(); } catch (Exception error) when (error is IOException or InvalidOperationException) { } }
    }

    /// <summary>The 44 bytes that start a WAV file of 16 kHz, mono, 16-bit sound with <paramref name="dataBytes"/> bytes of it.</summary>
    public static void WriteHeader(Stream stream, long dataBytes)
    {
        Span<byte> header = stackalloc byte[HeaderBytes];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)Math.Min(uint.MaxValue, 36 + dataBytes));
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 1);                       // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..], 1);                       // mono
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], WindowsMicrophone.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], BytesPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 2);                       // block align
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], (uint)Math.Min(uint.MaxValue, dataBytes));
        stream.Write(header);
    }
}
