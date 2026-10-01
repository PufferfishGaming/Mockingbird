using System.Text;
using TriAsr.Application;

namespace TriAsr.Audio;

public static class WaveAudio
{
    public static AudioInfo Inspect(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII);
        if (ReadTag(reader) != "RIFF") throw new InvalidDataException("Only RIFF WAV is supported.");
        reader.ReadUInt32();
        if (ReadTag(reader) != "WAVE") throw new InvalidDataException("Invalid WAV header.");
        int rate = 0, channels = 0, bits = 0;
        long bytes = -1;
        while (stream.Position + 8 <= stream.Length)
        {
            var tag = ReadTag(reader);
            var size = reader.ReadUInt32();
            var end = stream.Position + size;
            if (end > stream.Length) throw new InvalidDataException("Truncated WAV chunk.");
            if (tag == "fmt ")
            {
                if (size < 16 || reader.ReadUInt16() != 1) throw new InvalidDataException("Canonical WAV must be PCM.");
                channels = reader.ReadUInt16(); rate = reader.ReadInt32(); reader.ReadUInt32(); reader.ReadUInt16(); bits = reader.ReadUInt16();
            }
            else if (tag == "data") bytes = size;
            stream.Position = Math.Min(end + (size & 1), stream.Length);
        }
        if (rate != 16000 || channels != 1 || bits != 16 || bytes <= 0 || bytes % 2 != 0)
            throw new InvalidDataException("Canonical audio must be nonempty 16 kHz, mono, PCM signed 16-bit.");
        return new(rate, channels, bits, bytes / 2);
    }
    public static float[] ReadSamples(string path)
    {
        Inspect(path);
        using var reader = new BinaryReader(File.OpenRead(path));
        reader.BaseStream.Position = 12;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var tag = ReadTag(reader); var size = reader.ReadUInt32();
            if (tag == "data")
            {
                if (size > 512 * 1024 * 1024) throw new InvalidDataException("Worker audio chunk exceeds 512 MB.");
                var samples = new float[size / 2];
                for (var i = 0; i < samples.Length; i++) samples[i] = reader.ReadInt16() / 32768f;
                return samples;
            }
            reader.BaseStream.Position += size + (size & 1);
        }
        throw new InvalidDataException("WAV has no samples.");
    }
    private static string ReadTag(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));
    public static IEnumerable<float[]> ReadChunks(string path, int maximumSeconds)
    {
        Inspect(path);
        using var reader = new BinaryReader(File.OpenRead(path));
        reader.BaseStream.Position = 12;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var tag = ReadTag(reader); var size = reader.ReadUInt32();
            if (tag != "data") { reader.BaseStream.Position += size + (size & 1); continue; }
            long remaining = size / 2;
            while (remaining > 0)
            {
                var count = (int)Math.Min(remaining, maximumSeconds * 16000L);
                var samples = new float[count];
                for (var i = 0; i < count; i++) samples[i] = reader.ReadInt16() / 32768f;
                // Prefer a low-energy boundary in the last quarter; unread trailing samples go into the next chunk.
                if (remaining > count && count > 16000)
                {
                    var best = count; var lowest = double.MaxValue;
                    for (var start = count * 3 / 4; start < count - 1600; start += 160)
                    {
                        double energy = 0;
                        for (var i = start; i < start + 1600; i++) energy += samples[i] * samples[i];
                        if (energy < lowest) { lowest = energy; best = start + 800; }
                    }
                    reader.BaseStream.Position -= (count - best) * 2;
                    Array.Resize(ref samples, best); count = best;
                }
                remaining -= count;
                yield return samples;
            }
            yield break;
        }
    }
}

public sealed class FfmpegNormalizer(IProcessRunner runner, string executable) : IAudioNormalizer
{
    public async Task<AudioInfo> NormalizeAsync(string source, string destination, CancellationToken cancellationToken)
    {
        source = Path.GetFullPath(source); destination = Path.GetFullPath(destination);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Source media cannot be overwritten.");
        if (!File.Exists(source)) throw new FileNotFoundException("Source media is missing.", source);
        if (File.Exists(destination)) return WaveAudio.Inspect(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".wav";
        try
        {
            var result = await runner.RunAsync(new(executable,
                ["-nostdin", "-hide_banner", "-v", "error", "-n", "-threads", "2", "-i", source, "-map", "0:a:0", "-vn", "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", temporary],
                Path.GetDirectoryName(destination)!, TimeSpan.FromHours(6)), cancellationToken);
            if (result.ExitCode != 0) throw new InvalidOperationException($"FFmpeg exited with code {result.ExitCode}: {result.StandardError}");
            var audio = WaveAudio.Inspect(temporary);
            File.Move(temporary, destination);
            return audio;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
