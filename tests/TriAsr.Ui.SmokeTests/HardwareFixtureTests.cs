using System.IO;
using System.Text.Json;
using TriAsr.App;
using TriAsr.Hardware;

namespace TriAsr.Ui.SmokeTests;

/// <summary>
/// Real hardware snapshots (names and sizes only) kept as fixtures, so recommendations and the saved-profile format
/// are tested without the machine. Add a snapshot from every computer you support, for example the ARM laptop.
/// </summary>
public sealed class HardwareFixtureTests
{
    private static HardwareProfile Load(string name) =>
        JsonSerializer.Deserialize<HardwareProfile>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)))!;

    [Fact]
    public void AProfileSavedByAnOlderVersionStillLoadsWithSafeDefaults()
    {
        var profile = Load("hardware-x64-core-ultra7-radeon-rx7900xt.json");
        Assert.Equal("Intel(R) Core(TM) Ultra 7 270K Plus", profile.Cpu);
        Assert.Equal(8, profile.Topology.PerformanceCores);
        Assert.Equal(16, profile.Topology.EfficiencyCores);
        // Fields added later are absent from the old file and must default rather than fail or be guessed.
        Assert.Equal("unknown", profile.OsArchitecture);
        Assert.Equal("unknown", profile.PowerSource);
        Assert.Null(profile.ProbeErrors);
        Assert.Null(profile.Issues);
    }

    [Fact]
    public void TheRealWorkstationGetsTheTopRecommendation()
    {
        var choice = ModelRecommendation.For(Load("hardware-x64-core-ultra7-radeon-rx7900xt.json"));
        Assert.Equal("whisper-large-v3", choice.Whisper);
        Assert.Equal("canary-q8", choice.Canary);
        Assert.Equal("correction-q6", choice.Correction);
    }

    [Fact]
    public void TheNpuIsDetectedForDiagnosticsOnly()
    {
        var profile = Load("hardware-x64-core-ultra7-radeon-rx7900xt.json");
        Assert.Contains("AI Boost", Assert.Single(profile.NpuDevices));
        // Nothing in the recommendation depends on the NPU: the choice is identical without it.
        Assert.Equal(ModelRecommendation.For(profile), ModelRecommendation.For(profile with { NpuDevices = [] }));
    }

    [Fact]
    public void ANewProfileRoundTripsThroughJsonIncludingProbeErrors()
    {
        var profile = Load("hardware-x64-core-ultra7-radeon-rx7900xt.json") with
        {
            OsArchitecture = "x64", ProcessArchitecture = "x64", PowerSource = "ac",
            ProbeErrors = [new ProbeError("vulkan-devices", "InvalidOperationException", "x")], Issues = ["issue"]
        };
        var restored = JsonSerializer.Deserialize<HardwareProfile>(JsonSerializer.Serialize(profile))!;
        Assert.Equal("ac", restored.PowerSource);
        Assert.Equal("vulkan-devices", Assert.Single(restored.ProbeErrors!).Probe);
        Assert.Equal(["issue"], restored.Issues);
        Assert.Equal(profile.Fingerprint, restored.Fingerprint);
    }
}
