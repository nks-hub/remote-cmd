using Xunit;

namespace RemoteCmd.Tests;

public class ClientSessionTests
{
    [Fact]
    public void IsConnected_WhenLastPollRecent_ReturnsTrue()
    {
        var s = new ClientSession { LastPoll = DateTime.UtcNow };
        Assert.True(s.IsConnected());
    }

    [Fact]
    public void IsConnected_WhenLastPollOld_ReturnsFalse()
    {
        var s = new ClientSession { LastPoll = DateTime.UtcNow.AddSeconds(-30) };
        Assert.False(s.IsConnected());
    }

    [Fact]
    public void IsConnected_WhenNeverPolled_ReturnsFalse()
    {
        var s = new ClientSession();
        Assert.False(s.IsConnected());
    }

    [Fact]
    public void IsConnected_AtBoundary_ReturnsFalse()
    {
        var s = new ClientSession { LastPoll = DateTime.UtcNow.AddSeconds(-11) };
        Assert.False(s.IsConnected());
    }
}
