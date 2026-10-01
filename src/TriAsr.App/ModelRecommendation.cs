using TriAsr.Hardware;
namespace TriAsr.App;
public sealed record RecommendedModels(string Whisper, string Canary, string Correction, string Reason);
public static class ModelRecommendation
{
    public static RecommendedModels For(HardwareProfile profile)
    {
        const ulong gib = 1024UL * 1024 * 1024;
        var vram = profile.Gpus.MaxBy(gpu => gpu.DedicatedBytes)?.DedicatedBytes ?? 0;
        if (profile.VulkanDevices.Count > 0 && vram >= 16 * gib && profile.RamBytes >= 32 * gib)
            return new("whisper-large-v3", "canary-q8", "correction-q6", "Your Vulkan GPU and memory can accommodate the larger models. These are quality-focused starting candidates; tuning measures the fastest safe execution settings.");
        if (profile.VulkanDevices.Count > 0 && vram >= 8 * gib && profile.RamBytes >= 16 * gib)
            return new("whisper-large-v3-q5", "canary-q8", "correction-q4", "Compressed Whisper and correction models leave more GPU memory available. Canary Q8 keeps a larger speech model within this memory tier.");
        return new("whisper-large-v3-q5", "canary-q4", "correction-q4", "Compact models reduce memory use for CPU or smaller GPU systems. Tuning checks available CPU and Vulkan settings. Recommendations are memory-based starting points, not measured accuracy scores.");
    }
}
