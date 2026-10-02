using TriAsr.Application;

namespace TriAsr.Infrastructure;

public sealed class StoragePaths : IStoragePaths
{
    public StoragePaths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }
    public string Logs => Path.Combine(Root, "Logs");
    public string Database => Path.Combine(Root, "Config", "triasr.db");

    public void EnsureDirectories()
    {
        foreach (var relative in new[] { "Models/Whisper", "Models/Canary", "Models/Correction",
                     "Runtimes/Whisper-Vulkan", "Runtimes/Whisper-ROCm", "Runtimes/Canary",
                     "Runtimes/Llama", "Runtimes/FFmpeg", "Runtimes/YtDlp", "Jobs", "Output", "Temp", "Logs", "Config" })
            Directory.CreateDirectory(Path.Combine(Root, relative));
    }
}