using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace TriAsr.Infrastructure;

public sealed record ModelEntry(string Id, string Family, string Name, string Quantization, string Revision,
    long Bytes, string Sha256, string Url, string RelativePath, string License);
public sealed record DownloadProgress(long Received, long Total, double BytesPerSecond);
public sealed record ModelInstallation(bool Installed, bool Verified, long PartialBytes, bool WrongSize);
public static class ModelManifest
{
    private const string CanaryRevision = "e2d8e6d7f2accc1259dc5497b517b4083047e44b";
    private const string QwenRevision = "a06e946bb6b655725eafa393f4a9745d460374c9";
    public static IReadOnlyList<ModelEntry> Entries { get; } =
    [
        new("whisper-large-v3", "Whisper", "Large v3", "F16", "c521a4b02f422512d734391fdf08bb08c0862f68", 3095033483,
            "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2", "https://huggingface.co/ggerganov/whisper.cpp/resolve/c521a4b02f422512d734391fdf08bb08c0862f68/ggml-large-v3.bin", "models/ggml-large-v3.bin", "MIT"),
        new("whisper-large-v3-q5", "Whisper", "Large v3", "Q5_0", "c521a4b02f422512d734391fdf08bb08c0862f68", 1081140203,
            "d75795ecff3f83b5faa89d1900604ad8c780abd5739fae406de19f23ecd98ad1", "https://huggingface.co/ggerganov/whisper.cpp/resolve/c521a4b02f422512d734391fdf08bb08c0862f68/ggml-large-v3-q5_0.bin", "Models/Whisper/ggml-large-v3-q5_0.bin", "MIT"),
        Canary("canary-q4", "Q4_K_M", 735476448, "49e0a67e219bec95a254c2348460b6350e75a7ac6f93a131e48244b4c7cb53b9"),
        Canary("canary-q8", "Q8_0", 1144290016, "224f83d1bc487b3303b495a7d6874912fdece93de19d1a04b550829c30a5d289"),
        Canary("canary-f16", "F16", 1966111456, "eadda53cd1652d65cd12ff7ac4b7dc64cba1ce9837aae0c86e4222e8db89e320"),
        Qwen("correction-q4", "Q4_K_M", 2497281120, "3605803b982cb64aead44f6c1b2ae36e3acdb41d8e46c8a94c6533bc4c67e597"),
        Qwen("correction-q6", "Q6_K", 3306261600, "cd7b21b38b3e71400587c184b6a9b04d3beb4d13fdae6464d4075dee4f1bc5ad"),
        Qwen("correction-q8", "Q8_0", 4280405600, "391c1e410fd9f4cf2de2b510273b56a84c19ce18f4fa3bfb3774031dac4ef068")
    ];
    private static ModelEntry Canary(string id, string quant, long bytes, string hash) => new(id, "Canary", "Canary 1B v2", quant,
        CanaryRevision, bytes, hash, $"https://huggingface.co/handy-computer/canary-1b-v2-GGUF/resolve/{CanaryRevision}/canary-1b-v2-{quant}.gguf", $"Models/Canary/canary-1b-v2-{quant}.gguf", "CC-BY-4.0");
    private static ModelEntry Qwen(string id, string quant, long bytes, string hash) => new(id, "Correction", "Qwen3 4B Instruct 2507", quant,
        QwenRevision, bytes, hash, $"https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF/resolve/{QwenRevision}/Qwen3-4B-Instruct-2507-{quant}.gguf", $"Models/Correction/Qwen3-4B-Instruct-2507-{quant}.gguf", "Apache-2.0");
}
public sealed class ModelStore(string root, HttpClient? http = null) : IDisposable
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Bytes, long Ticks)> _verified = new();
    private sealed record VerificationReceipt(string Sha256, long Bytes, long LastWriteTicks);
    public ModelInstallation Inspect(ModelEntry model)
    {
        var file = new FileInfo(PathFor(model));
        var partial = new FileInfo(file.FullName + ".partial");
        return new(file.Exists, file.Exists && ReceiptMatches(model, file), partial.Exists ? partial.Length : 0,
            file.Exists && file.Length != model.Bytes);
    }
    private static bool ReceiptMatches(ModelEntry model, FileInfo file)
    {
        if (!file.Exists || file.Length != model.Bytes) return false;
        try
        {
            var receipt = JsonSerializer.Deserialize<VerificationReceipt>(File.ReadAllText(file.FullName + ".verified.json"));
            return receipt is not null && receipt.Sha256.Equals(model.Sha256, StringComparison.OrdinalIgnoreCase)
                && receipt.Bytes == file.Length && receipt.LastWriteTicks == file.LastWriteTimeUtc.Ticks;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }
    private static async Task SaveReceiptAsync(ModelEntry model, string path, CancellationToken token)
    {
        var file = new FileInfo(path);
        var receipt = new VerificationReceipt(model.Sha256, file.Length, file.LastWriteTimeUtc.Ticks);
        var temporary = path + ".verified.json.tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(receipt), token);
        File.Move(temporary, path + ".verified.json", true);
    }
    public async Task<bool> VerifyCachedAsync(ModelEntry model, CancellationToken token = default)
    {
        var file = new FileInfo(PathFor(model));
        if (!file.Exists) return false;
        if (_verified.TryGetValue(model.Id, out var previous) && previous == (file.Length, file.LastWriteTimeUtc.Ticks)) return true;
        if (ReceiptMatches(model, file)) { _verified[model.Id] = (file.Length, file.LastWriteTimeUtc.Ticks); return true; }
        if (!await VerifyAsync(model, token)) return false;
        _verified[model.Id] = (file.Length, file.LastWriteTimeUtc.Ticks); return true;
    }
    public string PathFor(ModelEntry model)
    {
        var directory = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, model.RelativePath));
        if (!path.StartsWith(directory, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Model path leaves the configured model root.");
        return path;
    }
    public async Task<bool> VerifyAsync(ModelEntry model, CancellationToken token = default)
    {
        var path = PathFor(model);
        if (!File.Exists(path) || new FileInfo(path).Length != model.Bytes) return false;
        await using var file = File.OpenRead(path);
        var valid = Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(model.Sha256, StringComparison.OrdinalIgnoreCase);
        if (valid) await SaveReceiptAsync(model, path, token);
        else { _verified.TryRemove(model.Id, out _); if (File.Exists(path + ".verified.json")) File.Delete(path + ".verified.json"); }
        return valid;
    }
    public async Task DownloadAsync(ModelEntry model, IProgress<DownloadProgress>? progress, CancellationToken token = default)
    {
        await _operation.WaitAsync(token);
        try
        {
            var destination = PathFor(model); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination))
            {
                if (await VerifyCachedAsync(model, token)) return;
                throw new InvalidDataException("An existing model has a different checksum. Move it aside before installing; existing files are preserved.");
            }
            var partial = destination + ".partial";
            var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (offset > model.Bytes) throw new InvalidDataException("The partial download is larger than expected. Remove it before retrying.");
            var free = new DriveInfo(Path.GetPathRoot(destination)!).AvailableFreeSpace;
            if (free < model.Bytes - offset + 128L * 1024 * 1024) throw new IOException("Insufficient free space for this model download.");
            if (offset < model.Bytes)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, model.Url);
                if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    if (response.Content.Headers.ContentRange?.From != offset || response.Content.Headers.ContentRange.Length != model.Bytes)
                        throw new InvalidDataException("The download server returned an inconsistent byte range.");
                }
                else offset = 0;
                await using var output = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 128 * 1024, true);
                output.SetLength(offset); output.Position = offset;
                await using var input = await response.Content.ReadAsStreamAsync(token);
                var buffer = new byte[128 * 1024]; var elapsed = Stopwatch.StartNew(); long received = offset;
                var previous = TimeSpan.Zero;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    received += read;
                    if (received > model.Bytes) throw new InvalidDataException("The downloaded model is larger than expected.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    if (elapsed.Elapsed - previous > TimeSpan.FromMilliseconds(250))
                    { progress?.Report(new(received, model.Bytes, (received - offset) / Math.Max(.001, elapsed.Elapsed.TotalSeconds))); previous = elapsed.Elapsed; }
                }
                await output.FlushAsync(token);
                if (received != model.Bytes) throw new EndOfStreamException("Model download was incomplete; resume is available.");
                progress?.Report(new(received, model.Bytes, (received - offset) / Math.Max(.001, elapsed.Elapsed.TotalSeconds)));
            }
            await using (var file = File.OpenRead(partial))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
                if (!hash.Equals(model.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Downloaded model failed SHA256. The partial file is retained for inspection; remove it before retrying.");
            }
            File.Move(partial, destination);
            await SaveReceiptAsync(model, destination, token);
        }
        finally { _operation.Release(); }
    }
    public void Dispose() { if (http is null) _http.Dispose(); _operation.Dispose(); }
}
