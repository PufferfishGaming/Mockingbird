using System.IO;
using System.Text.Json;
namespace TriAsr.App;
public sealed record ExecutionSettings(string WhisperBackend = "cpu", int WhisperThreads = 4, string CanaryBackend = "cpu", int CanaryThreads = 4,
    string CorrectionBackend = "cpu", int CorrectionThreads = 4, bool ParallelSpeech = false, string? Fingerprint = null);
public static class ExecutionSettingsStore
{
    public static async Task SaveAsync(string root, ExecutionSettings settings)
    {
        var path = Path.Combine(root, "Config", "active-execution.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(settings)); File.Move(path + ".tmp", path, true);
    }
    public static async Task<ExecutionSettings?> LoadAsync(string root)
    {
        var path = Path.Combine(root, "Config", "active-execution.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<ExecutionSettings>(await File.ReadAllTextAsync(path)) : null;
    }
}
