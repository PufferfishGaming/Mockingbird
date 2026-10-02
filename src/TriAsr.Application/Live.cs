namespace TriAsr.Application;

/// <summary>
/// Reads one spoken phrase for live dictation (ADR: live dictation). Where it runs is not the caller's business: on this computer's speech program, or on
/// a server that a Client is connected to.
/// </summary>
public interface ILiveRecognizer
{
    /// <param name="wav">The phrase as a WAV file (16 kHz, mono, 16-bit).</param>
    /// <param name="language">A language code, or <c>auto</c>.</param>
    /// <returns>What was said, cleaned of the program's own markers; empty when nothing could be made out.</returns>
    /// <exception cref="LiveException">The phrase could not be read. The message says why, in plain words (it is in English and is translated by the app).</exception>
    Task<string> RecognizeAsync(byte[] wav, string language, CancellationToken token);
}

/// <summary>A phrase could not be read: the speech model is missing, the program failed, the server did not answer.</summary>
public sealed class LiveException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The sentences live dictation says to a person from the layers below the interface; each is listed in <c>extra-keys.json</c> and translated by the app.</summary>
public static class LiveMessages
{
    public const string NoModel = "No speech model is downloaded yet. Download one on the Models page.";
    public const string Failed = "The phrase could not be recognised.";
    public const string TooLong = "The phrase is too long.";

    public static readonly IReadOnlyList<string> All = [NoModel, Failed, TooLong];
}
