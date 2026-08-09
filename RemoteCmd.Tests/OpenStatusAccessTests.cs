using System.Net;
using Xunit;

namespace RemoteCmd.Tests;

/// <summary>
/// --open-status hands the dashboard, the text of every command and its stored stdout to whoever
/// asks, with no token. That is defensible on this machine and nowhere else — but the relay itself
/// has to stay reachable, because clients poll it across the network.
/// </summary>
public class OpenStatusAccessTests
{
    private static readonly IPAddress Lan = IPAddress.Parse("192.0.2.10");

    [Fact]
    public void LocalCallerNeedsNoToken()
        => Assert.True(OpenStatus.AllowsAnonymous(true, true, null, IPAddress.Loopback));

    [Fact]
    public void RemoteCallerStillNeedsAToken()
        => Assert.False(OpenStatus.AllowsAnonymous(true, true, null, Lan));

    [Fact]
    public void ClientEndpointsAreNeverOpen()
        => Assert.False(OpenStatus.AllowsAnonymous(true, readOnlyPath: false, null, IPAddress.Loopback));

    [Fact]
    public void WithoutTheSwitchEvenLocalCallersNeedAToken()
        => Assert.False(OpenStatus.AllowsAnonymous(false, true, null, IPAddress.Loopback));

    /// <summary>
    /// A wrong token is a failed attempt wherever it comes from; letting it through here would give
    /// an attacker a throttle-free way to tell a wrong guess from a right one.
    /// </summary>
    [Fact]
    public void AWrongTokenIsNotTreatedAsAnonymous()
        => Assert.False(OpenStatus.AllowsAnonymous(true, true, "not-the-token", IPAddress.Loopback));

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]   // Kestrel reports v4 peers this way on a dual-stack socket
    [InlineData("192.0.2.10", false)]
    [InlineData("::ffff:192.0.2.10", false)]
    public void LoopbackIsRecognisedInEveryFormKestrelReports(string address, bool expected)
        => Assert.Equal(expected, OpenStatus.IsLocal(IPAddress.Parse(address)));

    [Fact]
    public void UnknownPeerIsNotLocal() => Assert.False(OpenStatus.IsLocal(null));
}
