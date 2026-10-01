using TriAsr.Infrastructure;

namespace TriAsr.Persistence.Tests;

public sealed class SleepGuardTests
{
    [Fact]
    public void AGuardCanBeBegunAndDisposedMoreThanOnce()
    {
        var guard = SleepGuard.Begin("Mockingbird Studio test");
        Assert.NotNull(guard);
        guard.Dispose();
        guard.Dispose();
    }

    [Fact]
    public void GuardsNestAndAreIndependent()
    {
        using var outer = SleepGuard.Begin("outer");
        var inner = SleepGuard.Begin("inner");
        inner.Dispose();
        using var another = SleepGuard.Begin("another");
    }

    [Fact]
    public async Task AGuardSurvivesAwaitsAcrossThreads()
    {
        using var guard = SleepGuard.Begin("across threads");
        await Task.Run(() => Thread.Sleep(20));
        await Task.Delay(20);
    }

    [Fact]
    public void AReasonWithUnusualCharactersIsAccepted()
    {
        using var guard = SleepGuard.Begin("Átírás folyamatban – árvíztűrő tükörfúrógép ✓");
        using var empty = SleepGuard.Begin("");
    }
}
