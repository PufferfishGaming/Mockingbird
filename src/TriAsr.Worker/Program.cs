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
    var result = CanaryNative.Transcribe(request.RuntimeDirectory, request.Model, request.Language, request.Backend,
        request.Threads, WaveAudio.ReadChunks(request.Audio, 20), audio.DurationSeconds,
        fraction => Console.Error.WriteLine("TRIASR_PROGRESS " + fraction.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
    await File.WriteAllTextAsync(request.Output + ".tmp", JsonSerializer.Serialize(result));
    File.Move(request.Output + ".tmp", request.Output, true);
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }
