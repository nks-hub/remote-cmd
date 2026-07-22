using System.Collections.Concurrent;
using Xunit;

namespace RemoteCmd.Tests;

public class DashboardTests
{
    [Fact]
    public void Render_ShowsClientsRunningQueuedAndUptime()
    {
        var now = DateTime.UtcNow;
        var clients = new ConcurrentDictionary<string, ClientSession>();

        var idle = new ClientSession { Id = "aaaaaaaa1111", Name = "idle-box", LastPoll = now };
        var busy = new ClientSession { Id = "bbbbbbbb2222", Name = "busy-box", LastPoll = now };
        busy.InFlight[new PendingCommand { Command = "running" }.RequestId] = new PendingCommand { Command = "running" };
        busy.CommandQueue.Enqueue(new PendingCommand { Command = "queued-1" });
        clients[idle.Id] = idle;
        clients[busy.Id] = busy;

        var text = Dashboard.Render(clients, 7899, noTls: true, startedUtc: now.AddMinutes(-2), nowUtc: now);

        Assert.Contains("Clients: 2 connected / 2 total", text);
        Assert.Contains("1 running, 1 queued", text);
        Assert.Contains("up 00:02:00", text);
        Assert.Contains("idle-box", text);
        Assert.Contains("busy-box", text);
    }

    [Fact]
    public void Render_MarksStaleClients()
    {
        var now = DateTime.UtcNow;
        var clients = new ConcurrentDictionary<string, ClientSession>();
        clients["x"] = new ClientSession { Id = "x", Name = "gone", LastPoll = now.AddSeconds(-30) };

        var text = Dashboard.Render(clients, 7899, noTls: true, startedUtc: now.AddMinutes(-1), nowUtc: now);

        Assert.Contains("STALE", text);
        Assert.Contains("30s!", text);
    }

    [Fact]
    public void Render_NoClients_ShowsEmptyNotice()
    {
        var clients = new ConcurrentDictionary<string, ClientSession>();
        var now = DateTime.UtcNow;

        var text = Dashboard.Render(clients, 7899, noTls: false, startedUtc: now, nowUtc: now);

        Assert.Contains("(no clients registered)", text);
        Assert.Contains("https", text);
    }
}
