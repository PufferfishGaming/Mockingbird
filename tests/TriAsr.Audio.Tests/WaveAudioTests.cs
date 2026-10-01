using TriAsr.Audio;

namespace TriAsr.Audio.Tests;

public sealed class WaveAudioTests
{
    [Fact]
    public void CanonicalPcmIsReadAndMalformedAudioIsRejected()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write("RIFF"u8); writer.Write(40); writer.Write("WAVEfmt "u8); writer.Write(16);
                writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
                writer.Write("data"u8); writer.Write(4); writer.Write((short)0); writer.Write(short.MinValue);
            }
            Assert.Equal(2, WaveAudio.Inspect(path).SampleCount);
            Assert.Equal(new float[] { 0, -1 }, WaveAudio.ReadSamples(path));
            File.WriteAllBytes(path, [0, 1, 2, 3, 4]);
            Assert.Throws<InvalidDataException>(() => WaveAudio.Inspect(path));
        }
        finally { File.Delete(path); }
    }
}
