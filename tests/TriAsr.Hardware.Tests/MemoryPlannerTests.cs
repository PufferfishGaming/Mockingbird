using TriAsr.Hardware;

namespace TriAsr.Hardware.Tests;

public sealed class MemoryPlannerTests
{
    [Fact]
    public void BudgetReservesTwentyPercentAndRejectsOverflow()
    {
        Assert.Equal(800UL, GpuMemoryPlanner.SafeBudget(1000));
        Assert.True(GpuMemoryPlanner.Fits(1000, 500, 100, 100, 100));
        Assert.False(GpuMemoryPlanner.Fits(1000, 500, 100, 100, 101));
        Assert.False(GpuMemoryPlanner.Fits(ulong.MaxValue, ulong.MaxValue, 1, 1, 1));
    }
}
