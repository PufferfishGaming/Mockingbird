using System.Text;

namespace TriAsr.Infrastructure;

/// <summary>One part of a multipart/form-data body. <see cref="Content"/> is valid until the next part is asked for.</summary>
public sealed class MultipartPart(string name, string? fileName, string? contentType, Stream content)
{
    public string Name { get; } = name;
    public string? FileName { get; } = fileName;
    public string? ContentType { get; } = contentType;
    public Stream Content { get; } = content;

    /// <summary>A small text field, read completely; a field longer than <paramref name="maxBytes"/> is an error.</summary>
    public async Task<string> ReadTextAsync(int maxBytes, CancellationToken token)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[2048];
        int read;
        while ((read = await Content.ReadAsync(buffer, token)) > 0)
        {
            if (memory.Length + read > maxBytes) throw new InvalidDataException($"The form field \"{Name}\" is too long.");
            memory.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(memory.ToArray());
    }
}

/// <summary>
/// Reads a multipart/form-data body as a stream, part by part, without holding a file in memory: the upload of a long recording goes from the
/// connection to disk in pieces. The boundary may fall anywhere across reads; binary content that merely looks like a boundary is content.
/// </summary>
public sealed class MultipartReader
{
    private const int MaxHeaderLine = 8 * 1024;
    private const int MaxPartHeaders = 16 * 1024;
    private readonly Stream _source;
    private readonly byte[] _delimiter;
    private readonly byte[] _buffer;
    private int _start, _end;
    private bool _eof, _finished;
    private PartStream? _current;

    public MultipartReader(Stream source, string boundary, int bufferSize = 64 * 1024)
    {
        if (boundary.Length is 0 or > 70 || boundary.EndsWith(' ') || !boundary.All(c => char.IsAsciiLetterOrDigit(c) || "'()+_,-./:=? ".Contains(c)))
            throw new ArgumentException("The multipart boundary is not valid.", nameof(boundary));
        _source = source;
        _delimiter = Encoding.ASCII.GetBytes("\r\n--" + boundary);
        _buffer = new byte[Math.Max(bufferSize, _delimiter.Length * 2 + 64)];
        // The body begins with the first delimiter without the line break that normally precedes it: pretend that line break was there.
        _buffer[0] = (byte)'\r'; _buffer[1] = (byte)'\n'; _end = 2;
    }

    /// <summary>The boundary named in a Content-Type of <c>multipart/form-data</c>, or null for any other type.</summary>
    public static string? BoundaryOf(string? contentType)
    {
        if (contentType is null) return null;
        var pieces = contentType.Split(';');
        if (!pieces[0].Trim().Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var piece in pieces.Skip(1))
        {
            var equals = piece.IndexOf('=');
            if (equals > 0 && piece[..equals].Trim().Equals("boundary", StringComparison.OrdinalIgnoreCase))
                return piece[(equals + 1)..].Trim().Trim('"');
        }
        return null;
    }

    /// <summary>The next part, or null after the closing boundary.</summary>
    public async Task<MultipartPart?> NextPartAsync(CancellationToken token)
    {
        if (_finished) return null;
        if (_current is not null) await _current.SkipAsync(token);
        await SeekDelimiterAsync(token);
        await EnsureAsync(2, token);
        if (_buffer[_start] == '-' && _buffer[_start + 1] == '-') { _finished = true; return null; }
        // Spaces and tabs may follow the boundary before the line break.
        while (true)
        {
            await EnsureAsync(2, token);
            if (_buffer[_start] == '\r' && _buffer[_start + 1] == '\n') { _start += 2; break; }
            if (_buffer[_start] is not (byte)' ' and not (byte)'\t') throw new InvalidDataException("A multipart boundary is followed by something else than a line break.");
            _start++;
        }
        string? name = null, fileName = null, contentType = null;
        var total = 0;
        while (true)
        {
            var line = await ReadLineAsync(token);
            total += line.Length + 2;
            if (total > MaxPartHeaders) throw new InvalidDataException("The headers of a multipart part are too long.");
            if (line.Length == 0) break;
            var colon = line.IndexOf(':');
            if (colon <= 0) throw new InvalidDataException("A multipart header line is not valid.");
            var header = line[..colon].Trim(); var value = line[(colon + 1)..].Trim();
            if (header.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase))
            {
                name = Parameter(value, "name"); fileName = Parameter(value, "filename*") is { } encoded ? DecodeExtended(encoded) : Parameter(value, "filename");
            }
            else if (header.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) contentType = value;
        }
        if (name is null) throw new InvalidDataException("A multipart part has no name.");
        _current = new PartStream(this);
        return new MultipartPart(name, fileName, contentType, _current);
    }

    private static string? Parameter(string header, string key)
    {
        // name="value" or name=value, after a semicolon; a quoted value may hold a backslash-escaped quote.
        var index = 0;
        while (index < header.Length)
        {
            var semicolon = header.IndexOf(';', index);
            if (semicolon < 0) return null;
            var begin = semicolon + 1;
            while (begin < header.Length && header[begin] == ' ') begin++;
            if (string.Compare(header, begin, key + "=", 0, key.Length + 1, StringComparison.OrdinalIgnoreCase) == 0)
            {
                var valueStart = begin + key.Length + 1;
                if (valueStart < header.Length && header[valueStart] == '"')
                {
                    var text = new StringBuilder();
                    for (var i = valueStart + 1; i < header.Length; i++)
                    {
                        if (header[i] == '\\' && i + 1 < header.Length) { text.Append(header[++i]); continue; }
                        if (header[i] == '"') return text.ToString();
                        text.Append(header[i]);
                    }
                    return text.ToString();
                }
                var stop = header.IndexOf(';', valueStart);
                return header[valueStart..(stop < 0 ? header.Length : stop)].Trim();
            }
            index = semicolon + 1;
        }
        return null;
    }

    private static string DecodeExtended(string value)
    {
        // RFC 5987: charset'language'percent-encoded
        var parts = value.Split('\'', 3);
        if (parts.Length != 3) return value;
        try { return Uri.UnescapeDataString(parts[2]); } catch (UriFormatException) { return parts[2]; }
    }

    private async Task<bool> FillAsync(CancellationToken token)
    {
        if (_eof) return false;
        if (_start > 0) { Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start); _end -= _start; _start = 0; }
        if (_end == _buffer.Length) return true;
        var read = await _source.ReadAsync(_buffer.AsMemory(_end), token);
        if (read == 0) { _eof = true; return false; }
        _end += read;
        return true;
    }

    private async Task EnsureAsync(int count, CancellationToken token)
    {
        while (_end - _start < count)
            if (!await FillAsync(token)) throw new InvalidDataException("The multipart body ended unexpectedly.");
    }

    /// <summary>Moves just past the next boundary line start; whatever lies before it (a part's unread rest, or the preamble) is dropped.</summary>
    private async Task SeekDelimiterAsync(CancellationToken token)
    {
        while (true)
        {
            var index = _buffer.AsSpan(_start, _end - _start).IndexOf(_delimiter);
            if (index >= 0) { _start += index + _delimiter.Length; return; }
            _start = Math.Max(_start, _end - (_delimiter.Length - 1));
            if (!await FillAsync(token)) throw new InvalidDataException("The multipart body ended before its closing boundary.");
        }
    }

    private async Task<string> ReadLineAsync(CancellationToken token)
    {
        while (true)
        {
            var index = _buffer.AsSpan(_start, _end - _start).IndexOf("\r\n"u8);
            if (index >= 0)
            {
                var line = Encoding.UTF8.GetString(_buffer, _start, index);
                _start += index + 2;
                return line;
            }
            if (_end - _start > MaxHeaderLine) throw new InvalidDataException("A multipart header line is too long.");
            if (!await FillAsync(token)) throw new InvalidDataException("The multipart body ended inside a header.");
        }
    }

    private sealed class PartStream(MultipartReader owner) : Stream
    {
        private bool _ended;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token = default)
        {
            if (_ended || destination.Length == 0) return 0;
            while (true)
            {
                var available = owner._end - owner._start;
                var index = owner._buffer.AsSpan(owner._start, available).IndexOf(owner._delimiter);
                int count;
                if (index == 0) { _ended = true; return 0; }
                if (index > 0) count = Math.Min(index, destination.Length);
                else
                {
                    // No whole boundary yet; the last bytes could be the start of one, so they are kept back.
                    var safe = available - (owner._delimiter.Length - 1);
                    if (safe <= 0)
                    {
                        if (!await owner.FillAsync(token)) throw new InvalidDataException("The multipart body ended before its closing boundary.");
                        continue;
                    }
                    count = Math.Min(safe, destination.Length);
                }
                owner._buffer.AsMemory(owner._start, count).CopyTo(destination);
                owner._start += count;
                return count;
            }
        }

        public async Task SkipAsync(CancellationToken token)
        {
            var scratch = new byte[8192];
            while (await ReadAsync(scratch, token) > 0) { }
        }
    }
}
