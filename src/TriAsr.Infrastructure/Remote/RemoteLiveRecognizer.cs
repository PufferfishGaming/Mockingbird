using TriAsr.Application;
using TriAsr.Domain;

namespace TriAsr.Infrastructure;

/// <summary>Reads the phrases of live dictation on the server a Client is connected to: each phrase goes to the server as a small WAV file and the words come back.</summary>
/// <param name="languagePairs">Whether the server reads a phrase in a choice of two languages (<see cref="RemoteServerInfo.LanguagePairs"/>). An older one is sent the first of the two.</param>
public sealed class RemoteLiveRecognizer(RemoteServerClient client, bool languagePairs = true) : ILiveRecognizer
{
    public async Task<LivePhrase> RecognizeAsync(byte[] wav, string language, string? recent, CancellationToken token)
    {
        if (!languagePairs && LanguageCatalog.TryParseChoice(language, out var codes) && codes.Count > 1) language = codes[0];
        try { return await client.LiveAsync(wav, language, recent, token).ConfigureAwait(false); }
        catch (RemoteException error) { throw new LiveException(error.Message, error); }
    }
}
