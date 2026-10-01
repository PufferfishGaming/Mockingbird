namespace TriAsr.Application;

/// <summary>
/// Makes the 16 kHz speech file and, alongside it, the optional listening copy for the review player.
/// The copy is started first and awaited last, so it adds little waiting, and its failure never changes the outcome:
/// the player simply falls back to the speech file.
/// </summary>
public static class AudioPreparation
{
    public static async Task<AudioInfo> NormalizeAsync(IAudioNormalizer audio, string source, string normalizedPath, string playbackPath, CancellationToken cancellationToken)
    {
        var listeningCopy = CreateQuietlyAsync(audio, source, playbackPath, cancellationToken);
        try { return await audio.NormalizeAsync(source, normalizedPath, cancellationToken); }
        finally { await listeningCopy; } // always observed, so nothing is left running in the background
    }

    private static async Task CreateQuietlyAsync(IAudioNormalizer audio, string source, string playbackPath, CancellationToken cancellationToken)
    {
        try { await audio.CreatePlaybackCopyAsync(source, playbackPath, cancellationToken); }
        catch (Exception error) when (error is not OperationCanceledException) { /* optional output */ }
    }
}
