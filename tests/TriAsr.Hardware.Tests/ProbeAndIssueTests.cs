using TriAsr.Hardware;

namespace TriAsr.Hardware.Tests;

public sealed class ProbeAndIssueTests
{
    [Fact]
    public void AFailedProbeYieldsItsDefaultAndARecordedError()
    {
        var probes = new ProbeRunner();
        Assert.Null(probes.Run<string>("gpu-query", () => throw new UnauthorizedAccessException("denied")));
        var error = Assert.Single(probes.Errors);
        Assert.Equal(new ProbeError("gpu-query", nameof(UnauthorizedAccessException), "denied"), error);
    }

    [Fact]
    public void AbsentHardwareIsAnEmptyResultNotAnError()
    {
        var probes = new ProbeRunner();
        Assert.Null(probes.Run<string>("npu", () => null));
        Assert.Empty(probes.Errors);
        Assert.Equal(42, probes.Run("answer", () => (int?)42));
        Assert.Empty(probes.Errors);
    }

    [Fact]
    public async Task AsynchronousProbesBehaveTheSameWay()
    {
        var probes = new ProbeRunner();
        Assert.Equal("ok", await probes.RunAsync("fine", () => Task.FromResult<string?>("ok")));
        Assert.Null(await probes.RunAsync<string>("broken", async () => { await Task.Yield(); throw new InvalidOperationException("boom"); }));
        Assert.Equal("broken", Assert.Single(probes.Errors).Probe);
    }

    [Fact]
    public void ACancelledProbeStaysACancellationAndIsNotSwallowed()
    {
        var probes = new ProbeRunner();
        Assert.Throws<OperationCanceledException>(() => probes.Run<string>("slow", () => throw new OperationCanceledException()));
        Assert.Empty(probes.Errors);
    }

    [Fact]
    public void VeryLongErrorMessagesAreTruncated()
    {
        var probes = new ProbeRunner();
        probes.Run<string>("noisy", () => throw new InvalidOperationException(new string('x', 5000)));
        Assert.Equal(300, probes.Errors[0].Message.Length);
    }

    [Theory]
    [InlineData((byte)0, "battery")]
    [InlineData((byte)1, "ac")]
    [InlineData((byte)255, "unknown")]
    [InlineData((byte)7, "unknown")]
    public void WindowsLineStatusMapsToAPowerSource(byte status, string expected) => Assert.Equal(expected, PowerSource.Map(status));

    [Fact]
    public void ThePowerSourceOfThisComputerIsOneOfTheKnownValues() =>
        Assert.Contains(PowerSource.Read(), new[] { PowerSource.Ac, PowerSource.Battery, PowerSource.Unknown });

    [Fact]
    public void RunningUnderEmulationIsReported()
    {
        var issues = HardwareIssues.Evaluate("arm64", "x64", []);
        var issue = Assert.Single(issues);
        Assert.Contains("x64 program running under emulation on a arm64 computer", issue);
        Assert.Contains("native arm64 version", issue);
    }

    [Theory]
    [InlineData("x64", "x64")]
    [InlineData("arm64", "ARM64")]
    [InlineData("unknown", "x64")]
    [InlineData("x64", "unknown")]
    public void MatchingOrUnknownArchitecturesAreNotAnIssue(string os, string process) => Assert.Empty(HardwareIssues.Evaluate(os, process, []));

    [Fact]
    public void FailedProbesBecomeReadableIssuesThatDoNotClaimTheHardwareIsAbsent()
    {
        var issues = HardwareIssues.Evaluate("x64", "x64", [new ProbeError("gpu-driver-and-npu-query", "InvalidOperationException", "exit 1")]);
        var issue = Assert.Single(issues);
        Assert.Contains("gpu driver and npu query", issue);
        Assert.Contains("does not mean the hardware is absent", issue);
    }
}
