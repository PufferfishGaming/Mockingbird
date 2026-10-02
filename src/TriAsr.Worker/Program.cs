using System.Text.Json;
using TriAsr.Application;
using TriAsr.Audio;
using TriAsr.Engine.Canary;

try
{
    if (args.Length != 2 || args[0] != "--canary") throw new ArgumentException("Expected --canary request.json");
    var request = JsonSerializer.Deserialize<CanaryRequest>(await File.ReadAllTextAsync(args[1]))
        ?? throw new InvalidDataException("Invalid worker request.");
    var audio = WaveAudio.Inspect(request.Audio);
    void Progress(double fraction) => Console.Error.WriteLine("TRIASR_PROGRESS " + fraction.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
    var result = request.Windows is null
        ? CanaryNative.Transcribe(request.RuntimeDirectory, request.Model, request.Language, request.Backend,
            request.Threads, WaveAudio.ReadChunks(request.Audio, 20), audio.DurationSeconds, Progress)
        // A speech window is read as one piece (the plan keeps it short enough); a long stretch without speech is split like the whole file used to be.
        : CanaryNative.TranscribeWindows(request.RuntimeDirectory, request.Model, request.Language, request.Backend, request.Threads,
            request.Windows, request.CheckpointDirectory,
            window => WaveAudio.ReadRange(request.Audio, window.StartMs, window.EndMs, window.IsSpeech ? 40 : 20), audio.DurationSeconds, Progress);
    await File.WriteAllTextAsync(request.Output + ".tmp", JsonSerializer.Serialize(result));
    File.Move(request.Output + ".tmp", request.Output, true);
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }
