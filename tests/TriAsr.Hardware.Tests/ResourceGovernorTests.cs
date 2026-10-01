using System.Runtime.Versioning;
using TriAsr.Hardware;

namespace TriAsr.Hardware.Tests;

[SupportedOSPlatform("windows")]
public sealed class ResourceGovernorTests
{
    private static ResourceGovernor Governor(int cores, Func<string>? power = null) => new(() => cores, power ?? (() => PowerSource.Ac));

    [Theory]
    [InlineData(1, 1, 1, 1)]
    [InlineData(2, 1, 1, 1)]
    [InlineData(4, 2, 2, 2)]
    [InlineData(6, 3, 3, 4)]
    [InlineData(8, 4, 4, 6)]
    [InlineData(12, 6, 8, 10)]   // a 12-core laptop: leaves a whole 4-core cluster free in Default
    [InlineData(16, 8, 12, 14)]
    [InlineData(24, 12, 20, 22)] // the reference workstation
    public void BudgetsFollowTheDocumentedRule(int cores, int quiet, int normal, int max)
    {
        Assert.Equal(quiet, ResourceGovernor.ThreadsFor(ResourceProfile.Quiet, cores));
        Assert.Equal(normal, ResourceGovernor.ThreadsFor(ResourceProfile.Default, cores));
        Assert.Equal(normal, ResourceGovernor.ThreadsFor(ResourceProfile.Auto, cores));
        Assert.Equal(max, ResourceGovernor.ThreadsFor(ResourceProfile.Max, cores));
    }

    [Fact]
    public void ProfilesAreOrderedAndNoProfileEverUsesEveryCoreOnARealMachine()
    {
        for (var cores = 1; cores <= 128; cores++)
        {
            var quiet = ResourceGovernor.ThreadsFor(ResourceProfile.Quiet, cores);
            var normal = ResourceGovernor.ThreadsFor(ResourceProfile.Default, cores);
            var max = ResourceGovernor.ThreadsFor(ResourceProfile.Max, cores);
            Assert.InRange(quiet, 1, normal);
            Assert.InRange(normal, quiet, max);
            Assert.InRange(max, normal, cores);
            if (cores >= 3) Assert.True(max < cores, $"{cores} cores: Max must leave a core free");
        }
    }

    [Fact]
    public void AutoUsesQuietOnBatteryAndDefaultOtherwise()
    {
        var power = PowerSource.Ac;
        var governor = Governor(24, () => power);
        Assert.Equal(ResourceProfile.Default, governor.Current().Effective);
        Assert.Equal(20, governor.Current().Threads);
        power = PowerSource.Battery;
        Assert.Equal(ResourceProfile.Quiet, governor.Current().Effective);
        Assert.Equal(12, governor.Current().Threads);
        power = PowerSource.Unknown;
        Assert.Equal(ResourceProfile.Default, governor.Current().Effective);
    }

    [Fact]
    public void AnExplicitChoiceIgnoresThePowerSource()
    {
        var governor = Governor(24, () => PowerSource.Battery) ;
        governor.Profile = ResourceProfile.Max;
        Assert.Equal(ResourceProfile.Max, governor.Current().Effective);
        Assert.Equal(22, governor.Current().Threads);
        Assert.Equal(12, governor.For(ResourceProfile.Quiet).Threads);
    }

    [Fact]
    public void ClampLimitsAThreadRequestToTheCurrentBudgetAndNeverGoesBelowOne()
    {
        var governor = Governor(24);
        Assert.Equal(20, governor.Clamp(24));
        Assert.Equal(20, governor.Clamp(20));
        Assert.Equal(8, governor.Clamp(8));
        Assert.Equal(1, governor.Clamp(0));
        Assert.Equal(1, governor.Clamp(-5));
        governor.Profile = ResourceProfile.Quiet;
        Assert.Equal(12, governor.Clamp(24));
    }

    [Fact]
    public void TheCoreCountIsReadOnceAndAZeroReadingIsTreatedAsOneCore()
    {
        var reads = 0;
        var governor = new ResourceGovernor(() => { reads++; return 0; }, () => PowerSource.Ac);
        Assert.Equal(1, governor.Current().Threads);
        Assert.Equal(1, governor.Clamp(8));
        Assert.Equal(1, reads);
    }

    [Fact]
    public void TheSummaryNamesTheProfileTheReasonAndTheLimit()
    {
        var auto = Governor(12, () => PowerSource.Battery).Current().Summary;
        Assert.Contains("Auto", auto); Assert.Contains("Quiet", auto); Assert.Contains("battery", auto); Assert.Contains("6 CPU threads", auto);
        var governor = Governor(12);
        governor.Profile = ResourceProfile.Max;
        var max = governor.Current().Summary;
        Assert.Contains("Max", max); Assert.Contains("10 CPU threads", max);
    }
}
