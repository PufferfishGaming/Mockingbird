using System.Security.Cryptography;
using System.Text;
using TriAsr.Infrastructure;

namespace TriAsr.Persistence.Tests;

public sealed class MultipartReaderTests
{
    /// <summary>Hands out the bytes a few at a time, the way a network does, so a boundary falls across reads.</summary>
    private sealed class TrickleStream(byte[] data, int maxChunk, int seed = 1) : Stream
    {
        private readonly Random _random = new(seed);
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = Math.Min(Math.Min(buffer.Length, data.Length - _position), _random.Next(1, maxChunk + 1));
            data.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private const string Boundary = "----MockingbirdBoundary7a3";

    private static byte[] Form(params (string Headers, byte[] Content)[] parts)
    {
        using var memory = new MemoryStream();
        void Write(string text) => memory.Write(Encoding.UTF8.GetBytes(text));
        Write("a preamble that is ignored\r\n");
        foreach (var (headers, content) in parts)
        {
            Write("--" + Boundary + "\r\n" + headers + "\r\n\r\n");
            memory.Write(content);
            Write("\r\n");
        }
        Write("--" + Boundary + "--\r\nan epilogue that is ignored\r\n");
        return memory.ToArray();
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }

    [Fact]
    public async Task FieldsAndAFileComeOutInOrderWithTheirNamesAndTypes()
    {
        var body = Form(
            ("Content-Disposition: form-data; name=\"language\"", "de"u8.ToArray()),
            ("Content-Disposition: form-data; name=\"file\"; filename=\"Meeting 3.wav\"\r\nContent-Type: audio/wav", [1, 2, 3, 4, 5]),
            ("Content-Disposition: form-data; name=\"response_format\"", "srt"u8.ToArray()));
        var reader = new MultipartReader(new TrickleStream(body, 7), Boundary);

        var first = await reader.NextPartAsync(default);
        Assert.Equal("language", first!.Name);
        Assert.Null(first.FileName);
        Assert.Equal("de", await first.ReadTextAsync(100, default));

        var file = await reader.NextPartAsync(default);
        Assert.Equal("file", file!.Name);
        Assert.Equal("Meeting 3.wav", file.FileName);
        Assert.Equal("audio/wav", file.ContentType);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, await ReadAllAsync(file.Content));

        var last = await reader.NextPartAsync(default);
        Assert.Equal("response_format", last!.Name);
        Assert.Equal("srt", await last.ReadTextAsync(100, default));
        Assert.Null(await reader.NextPartAsync(default));
        Assert.Null(await reader.NextPartAsync(default));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(17)] [InlineData(1000)]
    public async Task BinaryContentThatLooksLikeABoundaryIsContentWhereverTheReadsEnd(int chunk)
    {
        // Lines, dashes and a near-miss of the boundary inside the file, and a boundary-sized run at the very end.
        var tricky = new List<byte>();
        tricky.AddRange(RandomNumberGenerator.GetBytes(5000));
        tricky.AddRange(Encoding.ASCII.GetBytes("\r\n--" + Boundary[..^1] + "\r\n--\r\n\r\n--"));
        tricky.AddRange(RandomNumberGenerator.GetBytes(5000));
        tricky.AddRange(Encoding.ASCII.GetBytes("\r\n--" + Boundary[..^2]));
        var body = Form(("Content-Disposition: form-data; name=\"file\"; filename=\"x.bin\"", tricky.ToArray()));
        var reader = new MultipartReader(new TrickleStream(body, chunk, seed: chunk), Boundary, bufferSize: 512);
        var part = await reader.NextPartAsync(default);
        Assert.Equal(tricky.ToArray(), await ReadAllAsync(part!.Content));
        Assert.Null(await reader.NextPartAsync(default));
    }

    [Fact]
    public async Task ALargeFileIsStreamedWithoutBeingHeld()
    {
        var data = RandomNumberGenerator.GetBytes(6 * 1024 * 1024);
        var body = Form(("Content-Disposition: form-data; name=\"file\"; filename=\"big.wav\"", data));
        var reader = new MultipartReader(new TrickleStream(body, 40000), Boundary);
        var part = await reader.NextPartAsync(default);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)), Convert.ToHexString(SHA256.HashData(await ReadAllAsync(part!.Content))));
    }

    [Fact]
    public async Task APartThatIsNotReadIsSkippedWhenTheNextOneIsAskedFor()
    {
        var body = Form(("Content-Disposition: form-data; name=\"a\"", RandomNumberGenerator.GetBytes(300_000)), ("Content-Disposition: form-data; name=\"b\"", "wanted"u8.ToArray()));
        var reader = new MultipartReader(new TrickleStream(body, 4096), Boundary);
        await reader.NextPartAsync(default);
        var second = await reader.NextPartAsync(default);
        Assert.Equal("wanted", await second!.ReadTextAsync(100, default));
    }

    [Fact]
    public async Task QuotedAndEncodedFileNamesAreRead()
    {
        var body = Form(
            ("Content-Disposition: form-data; name=\"file\"; filename=\"say \\\"hi\\\".wav\"", [1]),
            ("Content-Disposition: form-data; name=\"file\"; filename=\"fallback.wav\"; filename*=UTF-8''%C3%81rv%C3%ADzt%C5%B1r%C5%91.wav", [2]),
            ("content-disposition: form-data; name=plain; filename=bare.wav", [3]));
        var reader = new MultipartReader(new TrickleStream(body, 9), Boundary);
        Assert.Equal("say \"hi\".wav", (await reader.NextPartAsync(default))!.FileName);
        Assert.Equal("Árvíztűrő.wav", (await reader.NextPartAsync(default))!.FileName);
        var bare = await reader.NextPartAsync(default);
        Assert.Equal("plain", bare!.Name);
        Assert.Equal("bare.wav", bare.FileName);
    }

    [Fact]
    public async Task ATruncatedBodyOrAFieldThatIsTooLongIsAnError()
    {
        var body = Form(("Content-Disposition: form-data; name=\"file\"; filename=\"x.wav\"", RandomNumberGenerator.GetBytes(10_000)));
        var cut = body[..(body.Length / 2)];
        var reader = new MultipartReader(new TrickleStream(cut, 100), Boundary);
        var part = await reader.NextPartAsync(default);
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAllAsync(part!.Content));

        var long_ = Form(("Content-Disposition: form-data; name=\"prompt\"", new byte[5000]));
        var second = await new MultipartReader(new TrickleStream(long_, 100), Boundary).NextPartAsync(default);
        await Assert.ThrowsAsync<InvalidDataException>(() => second!.ReadTextAsync(1000, default));
    }

    [Fact]
    public async Task APartWithoutANameOrWithAGarbledHeaderIsAnError()
    {
        var noName = Form(("Content-Type: text/plain", "x"u8.ToArray()));
        await Assert.ThrowsAsync<InvalidDataException>(() => new MultipartReader(new TrickleStream(noName, 50), Boundary).NextPartAsync(default));
        var garbled = Form(("this is not a header", "x"u8.ToArray()));
        await Assert.ThrowsAsync<InvalidDataException>(() => new MultipartReader(new TrickleStream(garbled, 50), Boundary).NextPartAsync(default));
    }

    [Fact]
    public void TheBoundaryComesFromTheContentTypeOfAForm()
    {
        Assert.Equal("abc123", MultipartReader.BoundaryOf("multipart/form-data; boundary=abc123"));
        Assert.Equal("a b", MultipartReader.BoundaryOf("Multipart/Form-Data; charset=utf-8; Boundary=\"a b\""));
        Assert.Null(MultipartReader.BoundaryOf("application/json"));
        Assert.Null(MultipartReader.BoundaryOf("multipart/form-data"));
        Assert.Null(MultipartReader.BoundaryOf(null));
        Assert.Throws<ArgumentException>(() => new MultipartReader(Stream.Null, new string('x', 71)));
        Assert.Throws<ArgumentException>(() => new MultipartReader(Stream.Null, "bad\r\nboundary"));
        Assert.Throws<ArgumentException>(() => new MultipartReader(Stream.Null, ""));
    }
}
