using System.IO;
using TriAsr.Application;
using TriAsr.Engine.Whisper;
using TriAsr.Hardware;
using TriAsr.Infrastructure;

namespace TriAsr.App;

/// <summary>
/// Reads the phrases of live dictation on this computer. It uses the smaller Whisper model when that is installed (about twice as fast) and the one the
/// transcriptions use otherwise, on the same graphics card or processor and with the same number of threads as a transcription would get. One phrase is read at a time,
/// so that dictating does not take the machine from a transcription that is running more than a moment.
/// </summary>
public sealed class LocalLiveRecognizer(IProcessRunner runner, RuntimePaths paths, IStoragePaths storage, ModelStore models, LocalTranscriptionStages stages, ResourceGovernor governor) : ILiveRecognizer
{
    private readonly SemaphoreSlim _one = new(1, 1);
    private (string Backend, int Threads, string Model, DateTime At)? _execution;

    /// <summary>The model dictation reads with, or null when none is installed.</summary>
    public string? Model()
    {
        var small = models.PathFor(ModelManifest.Entries.First(entry => entry.Id == "whisper-large-v3-q5"));
        if (File.Exists(small)) return small;
        return File.Exists(paths.WhisperModel) ? paths.WhisperModel : null;
    }

    /// <summary>Whether a phrase can be read at all: the program and a model are there.</summary>
    public bool IsReady => File.Exists(paths.Whisper) && Model() is not null;

    public async Task<string> RecognizeAsync(byte[] wav, string language, CancellationToken token)
    {
        var model = Model() ?? throw new LiveException(LiveMessages.NoModel);
        await _one.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_execution is not { } known || known.Model != model || DateTime.UtcNow - known.At > TimeSpan.FromMinutes(1))
            {
                var (chosenBackend, chosenThreads) = await stages.LiveExecutionAsync(model, token).ConfigureAwait(false);
                _execution = known = (chosenBackend, chosenThreads, model, DateTime.UtcNow);
            }
            var (backend, threads, _, _) = known;
            var executable = paths.WhisperFor(backend);
            if (!File.Exists(executable)) throw new LiveException(LiveMessages.Failed);
            var whisper = new LiveWhisper(runner, executable, model, governor.Clamp(threads), backend, Path.Combine(storage.Root, "Temp", "Live"));
            return await whisper.RecognizeAsync(wav, language, token).ConfigureAwait(false);
        }
        finally { _one.Release(); }
    }
}
