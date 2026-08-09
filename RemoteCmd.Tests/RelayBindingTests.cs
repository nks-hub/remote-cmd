using System.Net;
using System.Net.Sockets;
using Xunit;

namespace RemoteCmd.Tests;

/// <summary>
/// --open-status hands the dashboard, the text of every command and its stored stdout to anyone who
/// reaches the port, without a token. Nothing in the suite checked WHERE that port lives, so the
/// relay served all of it to the whole network while the code comments claimed it was localhost-only.
/// </summary>
public class RelayBindingTests
{
    [Theory]
    [InlineData(true, true, "http://127.0.0.1:7890")]
    [InlineData(true, false, "https://127.0.0.1:7890")]
    [InlineData(false, true, "http://0.0.0.0:7890")]
    [InlineData(false, false, "https://0.0.0.0:7890")]
    public void OpenStatusIsLoopbackOnly(bool openStatus, bool noTls, string expected)
        => Assert.Equal(expected, RelayBinding.UrlFor(openStatus, noTls, 7890));

    [Fact]
    public void ClosedRelayStillListensEverywhere()
    {
        // The default has to stay reachable: clients poll the relay across the network.
        Assert.Contains("0.0.0.0", RelayBinding.UrlFor(openStatus: false, noTls: true, 7890));
        Assert.DoesNotContain("127.0.0.1", RelayBinding.UrlFor(openStatus: false, noTls: true, 7890));
    }

    /// <summary>
    /// The string is only a promise; this checks the promise is one Kestrel actually keeps, by
    /// binding a socket the same way and proving a non-loopback address cannot reach it.
    /// </summary>
    [Fact]
    public void LoopbackUrlIsUnreachableFromANonLoopbackAddress()
    {
        var url = RelayBinding.UrlFor(openStatus: true, noTls: true, port: 0);
        var host = new Uri(url).Host;
        Assert.Equal("127.0.0.1", host);

        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Parse(host), 0));
        listener.Listen(1);
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        var external = Dns.GetHostAddresses(Dns.GetHostName())
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        if (external is null) return;   // machine has no non-loopback IPv4; nothing to prove here

        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var reached = probe.BeginConnect(new IPEndPoint(external, port), null, null)
                           .AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(2)) && probe.Connected;

        Assert.False(reached, $"a loopback-bound port answered on {external} — open-status would be network-wide");
    }
}
