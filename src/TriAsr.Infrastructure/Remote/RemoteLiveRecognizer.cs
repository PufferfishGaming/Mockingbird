using TriAsr.Application;

namespace TriAsr.Infrastructure;

/// <summary>Reads the phrases of live dictation on the server a Client is connected to: each phrase goes to the server as a small WAV file and the words come back.</summary>
public sealed class RemoteLiveRecognizer(RemoteServerClient client) : ILiveRecognizer
{
    public async Task<string> RecognizeAsync(byte[] wav, string language, CancellationToken token)
    {
        try { return await client.LiveAsync(wav, language, token).ConfigureAwait(false); }
        catch (RemoteException error) { throw new LiveException(error.Message, error); }
    }
}