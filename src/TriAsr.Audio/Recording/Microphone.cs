namespace TriAsr.Audio.Recording;

/// <summary>A microphone (or other recording device) the computer offers.</summary>
/// <param name="Id">Windows' number for it; <see cref="WindowsMicrophone.DefaultDevice"/> is whatever Windows uses by default.</param>
public sealed record InputDevice(int Id, string Name);

/// <summary>What a recording is made from. The one that matters is <see cref="WindowsMicrophone"/>; tests use a stand-in that plays back a prepared sound.</summary>
public interface IMicrophone
{
    /// <summary>The devices that can record, the default one first.</summary>
    IReadOnlyList<InputDevice> Devices();

    /// <summary>
    /// Starts recording from a device. The sound arrives as 16 kHz, mono, 16-bit PCM in pieces of about a tenth of a second, on a thread of its own;
    /// <paramref name="onFailure"/> is called there if the device goes away. Disposing the result stops the recording.
    /// </summary>
    IDisposable Start(int deviceId, Action<ReadOnlyMemory<byte>> onData, Action<Exception> onFailure);
}

/// <summary>The microphone could not be used. The message says what to do about it.</summary>
public sealed class MicrophoneException(string message, Exception? inner = null) : Exception(message, inner);
