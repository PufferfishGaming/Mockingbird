using TriAsr.Infrastructure;

namespace TriAsr.Persistence.Tests;

public sealed class AuthThrottleTests
{
    private sealed class Clock { public DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero); }

    [Fact]
    public void AfterTooManyWrongKeysAnAddressIsRefusedUntilTheLockoutEnds()
    {
        var clock = new Clock();
        var throttle = new AuthThrottle(3, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), () => clock.Now);
        for (var i = 0; i < 2; i++) { Assert.False(throttle.IsBlocked("10.0.0.5")); throttle.RecordFailure("10.0.0.5"); }
        Assert.False(throttle.IsBlocked("10.0.0.5"));
        throttle.RecordFailure("10.0.0.5");
        Assert.True(throttle.IsBlocked("10.0.0.5"));
        Assert.False(throttle.IsBlocked("10.0.0.6"));   // another address is not affected
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.True(throttle.IsBlocked("10.0.0.5"));
        clock.Now += TimeSpan.FromMinutes(1.1);
        Assert.False(throttle.IsBlocked("10.0.0.5"));
    }

    [Fact]
    public void FailuresSpreadOverALongTimeDoNotAddUp()
    {
        var clock = new Clock();
        var throttle = new AuthThrottle(3, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), () => clock.Now);
        for (var i = 0; i < 10; i++) { throttle.RecordFailure("a"); clock.Now += TimeSpan.FromSeconds(40); Assert.False(throttle.IsBlocked("a")); }
    }

    [Fact]
    public void ARightKeyForgetsTheEarlierMistakes()
    {
        var throttle = new AuthThrottle(3);
        throttle.RecordFailure("a"); throttle.RecordFailure("a");
        throttle.RecordSuccess("a");
        throttle.RecordFailure("a"); throttle.RecordFailure("a");
        Assert.False(throttle.IsBlocked("a"));
    }

    [Fact]
    public void KeysAreComparedWholeAndANewKeyIsLongAndAlwaysDifferent()
    {
        Assert.True(AuthThrottle.SecretsEqual("mbk-abc", "mbk-abc"));
        Assert.False(AuthThrottle.SecretsEqual("mbk-abd", "mbk-abc"));
        Assert.False(AuthThrottle.SecretsEqual("mbk-ab", "mbk-abc"));
        Assert.False(AuthThrottle.SecretsEqual(null, "mbk-abc"));
        Assert.False(AuthThrottle.SecretsEqual("", ""));          // no key set means nothing matches
        var keys = Enumerable.Range(0, 200).Select(_ => AuthThrottle.NewKey()).ToArray();
        Assert.Equal(200, keys.Distinct().Count());
        Assert.All(keys, key => { Assert.StartsWith("mbk-", key); Assert.Equal(44, key.Length); Assert.Matches("^[A-Za-z0-9-]+$", key); });
    }
}
