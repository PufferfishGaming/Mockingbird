using System.Buffers.Binary;

namespace TriAsr.Application;

/// <summary>Cuts a stretch out of a job's normalized recording (16 kHz, mono, 16-bit WAV) as a WAV file of its own, for a speech program to read on its own.</summary>
public static class WaveSlice
{
    private const int BytesPerMs = 32;

    /// <exception cref="InvalidDataException">The file is not a 16 kHz mono 16-bit WAV file.</exception>
    public static byte[] Cut(string path, long startMs, long endMs)
    {
        using var stream = File.OpenRead(path);
        var (dataStart, dataLength) = FindData(stream);
        var from = Math.Clamp(startMs * BytesPerMs, 0, dataLength);
        var to = Math.Clamp(endMs * BytesPerMs, from, dataLength);
        var pcm = new byte[to - from];
        stream.Position = dataStart + from;
        stream.ReadExactly(pcm);
        return PhraseText.Wav(pcm);
    }

    /// <summary>Writes the stretch to <paramref name="target"/>.</summary>
    public static void Write(string path, long startMs, long endMs, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, Cut(path, startMs, endMs));
    }

    private static (long Start, long Length) FindData(Stream stream)
    {
        Span<byte> header = stackalloc byte[12];
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8)) throw new InvalidDataException("The recording is not a WAV file.");
        Span<byte> chunk = stackalloc byte[8];
        Span<byte> fmt = stackalloc byte[16];
        var format = false;
        while (stream.Position + 8 <= stream.Length)
        {
            stream.ReadExactly(chunk);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (chunk[..4].SequenceEqual("fmt "u8))
            {
                stream.ReadExactly(fmt);
                format = BinaryPrimitives.ReadUInt16LittleEndian(fmt) == 1 && BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]) == 1
                    && BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]) == 16_000 && BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]) == 16;
                stream.Position += size - 16 + (size & 1);
            }
            else if (chunk[..4].SequenceEqual("data"u8))
            {
                if (!format) throw new InvalidDataException("The recording is not 16 kHz mono 16-bit sound.");
                return (stream.Position, Math.Min(size, stream.Length - stream.Position) & ~1L);
            }
            else stream.Position += size + (size & 1);
        }
        throw new InvalidDataException("The recording has no sound in it.");
    }
}
