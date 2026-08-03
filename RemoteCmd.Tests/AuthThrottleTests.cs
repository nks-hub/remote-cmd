using Xunit;

namespace RemoteCmd.Tests;

public class AuthThrottleTests
{
    private static readonly DateTime T0 = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void LocksOutAfterTooManyFailures()
    {
        var throttle = new AuthThrottle();

        for (var i = 1; i < AuthThrottle.MaxFailures; i++)
            Assert.False(throttle.RecordFailure("10.0.0.1", T0));

        Assert.True(throttle.RecordFailure("10.0.0.1", T0));
        Assert.True(throttle.IsLockedOut("10.0.0.1", T0));
        Assert.False(throttle.IsLockedOut("10.0.0.2", T0));
    }

    [Fact]
    public void LockoutExpires()
    {
        var throttle = new AuthThrottle();
        for (var i = 0; i < AuthThrottle.MaxFailures; i++) throttle.RecordFailure("10.0.0.1", T0);

        Assert.True(throttle.IsLockedOut("10.0.0.1", T0.AddMinutes(AuthThrottle.LockoutMinutes - 1)));
        Assert.False(throttle.IsLockedOut("10.0.0.1", T0.AddMinutes(AuthThrottle.LockoutMinutes + 1)));
    }

    [Fact]
    public void FailuresSpreadOverTimeDoNotAccumulate()
    {
        var throttle = new AuthThrottle();

        for (var i = 0; i < AuthThrottle.MaxFailures * 3; i++)
            Assert.False(throttle.RecordFailure("10.0.0.1", T0.AddMinutes(i * 2)));

        Assert.False(throttle.IsLockedOut("10.0.0.1", T0.AddMinutes(60)));
    }

    [Fact]
    public void LockoutIsPerPeer()
    {
        var throttle = new AuthThrottle();
        for (var i = 0; i < AuthThrottle.MaxFailures; i++) throttle.RecordFailure("10.0.0.1", T0);

        Assert.True(throttle.IsLockedOut("10.0.0.1", T0));
        Assert.False(throttle.IsLockedOut("10.0.0.9", T0));
    }

    [Fact]
    public void PruneKeepsLockedPeersAndDropsStaleOnes()
    {
        var throttle = new AuthThrottle();
        for (var i = 0; i < AuthThrottle.MaxFailures; i++) throttle.RecordFailure("locked", T0);
        throttle.RecordFailure("stale", T0);

        Assert.Equal(1, throttle.Prune(T0.AddMinutes(2)));
        Assert.True(throttle.IsLockedOut("locked", T0.AddMinutes(2)));
    }
}
