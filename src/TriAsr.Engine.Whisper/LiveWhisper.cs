using System.Globalization;
using TriAsr.Application;

namespace TriAsr.Engine.Whisper;

/// <summary>
/// Reads one phrase of live dictation with the same <c>whisper-cli</c> that transcribes whole recordings, started once per phrase. With the smaller
/// model on a graphics card that takes a second or two for a phrase of a few seconds, which is fast enough to type it while the next one is spoken.
/// </summary>
public sealed class LiveWhisper(IProcessRunner runner, string executable, string model, int threads, string backend, string folder) : ILiveRecognizer
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public async Task<string> RecognizeAsync(byte[] wav, string language, CancellationToken token)
    {
        if (!File.Exists(model)) throw new LiveException(LiveMessages.NoModel);
        if (wav.Length > 60L * 32_000 + 44) throw new LiveException(LiveMessages.TooLong);       // a minute of sound: far more than a phrase
        Directory.CreateDirectory(folder);
        var name = Path.Combine(folder, Guid.NewGuid().ToString("N"));
        var audio = name + ".wav";
        var arguments = new List<string>
        {
            "-m", model, "-f", audio, "-l", string.IsNullOrWhiteSpace(language) ? "auto" : language, "-t", threads.ToString(CultureInfo.InvariantCulture),
            "-mc", "0", "-nt", "-np", "-otxt", "-of", name
        };
        if (backend == "cpu") arguments.Add("-ng");
        try
        {
            await File.WriteAllBytesAsync(audio, wav, token).ConfigureAwait(false);
            var result = await runner.RunAsync(new(executable, arguments, Path.GetDirectoryName(executable)!, Timeout), token).ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(name + ".txt")) throw new LiveException(LiveMessages.Failed);
            return PhraseText.Clean(await File.ReadAllTextAsync(name + ".txt", token).ConfigureAwait(false));
        }
        finally
        {
            foreach (var path in new[] { audio, name + ".txt" })
                try { File.Delete(path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
