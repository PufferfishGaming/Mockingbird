using System.Globalization;
using System.Text;

namespace TriAsr.Infrastructure;

/// <summary>Reads from a connection through a buffer that starts with whatever arrived after the headers.</summary>
internal sealed class ConnectionReader
{
    private readonly Stream _network;
    private readonly byte[] _buffer;
    private int _start, _end;

    public ConnectionReader(Stream network, ReadOnlySpan<byte> leftover)
    {
        _network = network;
        _buffer = new byte[Math.Max(16 * 1024, leftover.Length)];
        leftover.CopyTo(_buffer);
        _end = leftover.Length;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token)
    {
        if (destination.Length == 0) return 0;
        if (_start == _end)
        {
            if (destination.Length >= _buffer.Length) return await _network.ReadAsync(destination, token);
            _start = 0;
            _end = await _network.ReadAsync(_buffer, token);
            if (_end == 0) return 0;
        }
        var count = Math.Min(destination.Length, _end - _start);
        _buffer.AsMemory(_start, count).CopyTo(destination);
        _start += count;
        return count;
    }

    /// <summary>One line without its CRLF. A line longer than <paramref name="maxLength"/> or ending the connection first is a protocol error.</summary>
    public async Task<string> ReadLineAsync(int maxLength, CancellationToken token)
    {
        var line = new List<byte>(32);
        var one = new byte[1];
        while (true)
        {
            if (await ReadAsync(one, token) == 0) throw new HttpProtocolException(400, "bad_request", "The request ended in the middle of a line.");
            if (one[0] == (byte)'\n')
            {
                if (line.Count == 0 || line[^1] != (byte)'\r') throw new HttpProtocolException(400, "bad_request", "A line of the request does not end with CRLF.");
                line.RemoveAt(line.Count - 1);
                return Encoding.Latin1.GetString(line.ToArray());
            }
            line.Add(one[0]);
            if (line.Count > maxLength) throw new HttpProtocolException(400, "bad_request", "A line of the request is too long.");
        }
    }
}

/// <summary>
/// The body of a request, read as it arrives: a fixed length or chunked, never more than the limit, and never waiting longer than the idle time
/// for the next piece. "Expect: 100-continue" is answered on the first read, so a request the handler refuses is never uploaded.
/// </summary>
internal sealed class RequestBodyStream : Stream
{
    private readonly ConnectionReader _reader;
    private readonly bool _chunked;
    private readonly long _max;
    private readonly TimeSpan _idle;
    private readonly Func<CancellationToken, Task>? _beforeFirstRead;
    private long _remaining;        // fixed length: bytes still to come
    private long _chunkRemaining;   // chunked: bytes of the current chunk still to come
    private bool _started;

    public RequestBodyStream(ConnectionReader reader, long? length, bool chunked, long max, TimeSpan idle, Func<CancellationToken, Task>? beforeFirstRead)
    {
        _reader = reader; _chunked = chunked; _max = max; _idle = idle; _beforeFirstRead = beforeFirstRead;
        _remaining = length ?? 0;
        IsComplete = !chunked && (length ?? 0) == 0;
    }

    public bool IsComplete { get; private set; }
    public long BytesRead { get; private set; }

    /// <summary>The client asked "Expect: 100-continue" and has not been told to go ahead: it is not sending the body, so there is nothing to wait for.</summary>
    public bool AwaitingContinue => _beforeFirstRead is not null && !_started;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        if (IsComplete || buffer.Length == 0) return 0;
        if (!_started)
        {
            _started = true;
            if (_beforeFirstRead is not null) await _beforeFirstRead(token);
        }
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        idle.CancelAfter(_idle);
        try
        {
            var read = _chunked ? await ReadChunkedAsync(buffer, idle.Token) : await ReadFixedAsync(buffer, idle.Token);
            BytesRead += read;
            if (BytesRead > _max) throw new HttpProtocolException(413, "payload_too_large", "The upload is larger than this server accepts.");
            return read;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new HttpProtocolException(408, "request_timeout", "The upload stalled for too long.");
        }
        catch (IOException)
        {
            throw new HttpProtocolException(400, "connection_closed", "The connection was closed during the upload.");
        }
    }

    private async ValueTask<int> ReadFixedAsync(Memory<byte> buffer, CancellationToken token)
    {
        var read = await _reader.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], token);
        if (read == 0) throw new HttpProtocolException(400, "bad_request", "The request body is shorter than its Content-Length.");
        _remaining -= read;
        if (_remaining == 0) IsComplete = true;
        return read;
    }

    private async ValueTask<int> ReadChunkedAsync(Memory<byte> buffer, CancellationToken token)
    {
        if (_chunkRemaining == 0)
        {
            var header = await _reader.ReadLineAsync(256, token);
            var semicolon = header.IndexOf(';');
            var size = semicolon >= 0 ? header[..semicolon] : header;
            if (size.Length is 0 or > 15 || !long.TryParse(size.Trim(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var chunk) || chunk < 0)
                throw new HttpProtocolException(400, "bad_request", "A chunk of the request body has an invalid size.");
            if (chunk == 0)
            {
                for (var trailers = 0; trailers < 100; trailers++)
                    if ((await _reader.ReadLineAsync(8192, token)).Length == 0) { IsComplete = true; return 0; }
                throw new HttpProtocolException(400, "bad_request", "The request has too many trailer lines.");
            }
            _chunkRemaining = chunk;
        }
        var read = await _reader.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _chunkRemaining)], token);
        if (read == 0) throw new HttpProtocolException(400, "bad_request", "The request body ended in the middle of a chunk.");
        _chunkRemaining -= read;
        if (_chunkRemaining == 0 && (await _reader.ReadLineAsync(2, token)).Length != 0)
            throw new HttpProtocolException(400, "bad_request", "A chunk of the request body is not followed by CRLF.");
        return read;
    }

    /// <summary>Reads and discards what is left of the body, so that the answer to a refused upload can be delivered. False when it is more than <paramref name="limit"/> or takes too long.</summary>
    public async Task<bool> DrainAsync(long limit, TimeSpan time)
    {
        using var timeout = new CancellationTokenSource(time);
        var scratch = new byte[16 * 1024];
        var drained = 0L;
        try
        {
            while (!IsComplete)
            {
                var read = await ReadAsync(scratch, timeout.Token);
                drained += read;
                if (read == 0 || drained > limit) return IsComplete;
            }
            return true;
        }
        catch (Exception error) when (error is OperationCanceledException or HttpProtocolException or IOException) { return false; }
    }
}
