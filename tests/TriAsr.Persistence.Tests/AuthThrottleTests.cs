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
    public void PasswordsAreComparedWholeAndANewOneIsEasyToTypeAndAlwaysDifferent()
    {
        Assert.True(AuthThrottle.SecretsEqual("k7m2-pq9x-w4hd", "k7m2-pq9x-w4hd"));
        Assert.False(AuthThrottle.SecretsEqual("k7m2-pq9x-w4he", "k7m2-pq9x-w4hd"));
        Assert.False(AuthThrottle.SecretsEqual("k7m2-pq9x", "k7m2-pq9x-w4hd"));
        Assert.False(AuthThrottle.SecretsEqual(null, "k7m2-pq9x-w4hd"));
        Assert.False(AuthThrottle.SecretsEqual("", ""));          // no password set means nothing matches here (the server is then open instead)
        var passwords = Enumerable.Range(0, 200).Select(_ => AuthThrottle.NewPassword()).ToArray();
        Assert.Equal(200, passwords.Distinct().Count());
        Assert.All(passwords, password => Assert.Matches("^[a-hj-km-np-z2-9]{4}-[a-hj-km-np-z2-9]{4}-[a-hj-km-np-z2-9]{4}$", password));   // no letters that look alike
    }
}
