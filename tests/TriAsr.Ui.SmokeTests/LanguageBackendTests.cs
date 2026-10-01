using System.IO;
using TriAsr.Domain;
using TriAsr.App;
using TriAsr.Hardware;
using TriAsr.Engine.Whisper;
namespace TriAsr.Ui.SmokeTests;
public sealed class LanguageBackendTests
{
    [Fact]
    public void CoverageDoesNotPretendEveryLanguageHasTwoEngines()
    {
        Assert.Equal(100, LanguageCatalog.All.Count); Assert.Equal(100, LanguageCatalog.All.Select(language => language.Code).Distinct().Count());
        Assert.Equal(25, LanguageCatalog.All.Count(language => language.DualEngine));
        Assert.True(LanguageCatalog.Supports("ja")); Assert.True(LanguageCatalog.Supports("yue"));
        Assert.False(LanguageCatalog.CanaryCodes.Contains("ja")); Assert.True(LanguageCatalog.CanaryCodes.Contains("fr"));
        Assert.False(LanguageCatalog.Supports("invalid"));
    }
    [Theory]
    [InlineData("using Vulkan0", "vulkan")]
    [InlineData("using CUDA0\nggml_cuda_init: found 1 CUDA devices", "cuda")]
    [InlineData("using CUDA0\nggml_cuda_init: found 1 ROCm devices", "rocm")]
    [InlineData("using HIP0", "rocm")]
    [InlineData("ggml_cuda_init: found 1 CUDA devices\nusing CPU", "cpu")]
    public void ActualBackendRequiresGpuExecutionInsteadOfMerelySeeingADriver(string log, string backend) => Assert.Equal(backend, WhisperEngine.IdentifyBackend(log));
    [Fact]
    public async Task AppliedSettingsSurviveReloadSeparatelyFromUnappliedTuningResults()
    {
        var root = Path.Combine(Path.GetTempPath(), "TriAsr.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Config"));
            await File.WriteAllTextAsync(Path.Combine(root, "Config", "tuning-results.json"), "{}");
            Assert.Null(await ExecutionSettingsStore.LoadAsync(root));
            var settings = new ExecutionSettings("vulkan", 8, "cpu", 4, "rocm", 8, false, "fixture");
            await ExecutionSettingsStore.SaveAsync(root, settings);
            Assert.Equal(settings, await ExecutionSettingsStore.LoadAsync(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void BackendEligibilityUsesHardwareAndRejectsUnsafeFolderKeys()
    {
        var hardware = new HardwareProfile("test", "test", new(8, 16, 8, 0), 64UL * 1073741824, true, true, true, true,
            [new("AMD", 0x1002, 20UL * 1073741824, "test")], ["Vulkan"], "unvalidated", [], "Windows", 100L * 1073741824, "fixture");
        Assert.True(BackendRuntimes.HardwareFits(hardware, "rocm")); Assert.False(BackendRuntimes.HardwareFits(hardware, "cuda"));
        Assert.True(BackendRuntimes.HardwareFits(hardware, "cpu"));
        Assert.Throws<ArgumentException>(() => BackendRuntimes.Folder(new RuntimePaths(), "Whisper", "../../escape"));
    }
}
