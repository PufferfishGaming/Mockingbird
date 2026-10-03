using System.Buffers.Binary;
using TriAsr.Application;
using TriAsr.Audio.Recording;

namespace TriAsr.Ui.SmokeTests;

/// <summary>A microphone that hears what the test plays to it, in pieces of a tenth of a second as the real one does.</summary>
internal sealed class FakeRoom : IMicrophone
{
    private Action<ReadOnlyMemory<byte>>? _onData;
    private Action<Exception>? _onFailure;
    public Exception? CannotOpen { get; set; }
    public int? Opened { get; private set; }
    public bool Open { get; private set; }

    public IReadOnlyList<InputDevice> Devices() => [new(WindowsMicrophone.DefaultDevice, ""), new(0, "Desk microphone"), new(1, "Headset")];

    public IDisposable Start(int deviceId, Action<ReadOnlyMemory<byte>> onData, Action<Exception> onFailure)
    {
        if (CannotOpen is not null) throw CannotOpen;
        Opened = deviceId; _onData = onData; _onFailure = onFailure; Open = true;
        return new Handle(this);
    }

    public void Hear(byte[] sound)
    {
        for (var at = 0; at < sound.Length; at += 3200) _onData!(sound.AsMemory(at, Math.Min(3200, sound.Length - at)));
    }

    public void Unplug() => _onFailure!(new MicrophoneException("The microphone is being used by another program. Close that program, or choose another microphone."));

    private sealed class Handle(FakeRoom room) : IDisposable { public void Dispose() => room.Open = false; }
}

/// <summary>Stands in for the speech program: it answers what the test says and records what it was given.</summary>
internal sealed class FakeReader : ILiveRecognizer
{
    public Func<int, byte[], string, Task<string>> Read { get; set; } = (_, _, _) => Task.FromResult("Hello there.");

    /// <summary>The language the answer of each phrase (numbered from 1) is in.</summary>
    public Func<int, string> Spoken { get; set; } = _ => "";
    public List<(byte[] Wav, string Language, string? Recent)> Calls { get; } = [];

    public async Task<LivePhrase> RecognizeAsync(byte[] wav, string language, string? recent, CancellationToken token)
    {
        int number;
        lock (Calls) { Calls.Add((wav, language, recent)); number = Calls.Count; }
        return new(await Read(number, wav, language), Spoken(number));
    }
}

/// <summary>Sound for the microphone: a tone (speech), the noise of a quiet room, and phrases made of them.</summary>
internal static class Sound
{
    private const int Rate = 16_000;

    public static byte[] Tone(double seconds, double rms)
    {
        var bytes = new byte[(int)(seconds * Rate) * 2];
        var peak = rms * Math.Sqrt(2) * 32767;
        for (var i = 0; i < bytes.Length / 2; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)(Math.Sin(2 * Math.PI * 220 * i / Rate) * peak));
        return bytes;
    }

    public static byte[] Quiet(double seconds)
    {
        var random = new Random(7);
        var bytes = new byte[(int)(seconds * Rate) * 2];
        for (var i = 0; i < bytes.Length / 2; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)((random.NextDouble() * 2 - 1) * 60));
        return bytes;
    }

    public static byte[] Join(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    /// <summary>A phrase of speech with pauses around it.</summary>
    public static byte[] Phrase(double seconds = 1.2) => Join(Quiet(0.5), Tone(seconds, 0.08), Quiet(1.2));
}
