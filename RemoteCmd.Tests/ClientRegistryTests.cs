using System.Collections.Concurrent;
using Xunit;

namespace RemoteCmd.Tests;

public class ClientRegistryTests
{
    [Fact]
    public void PruneStale_RemovesSessionsOlderThanThreshold()
    {
        var now = DateTime.UtcNow;
        var dict = new ConcurrentDictionary<string, ClientSession>();
        dict["recent"] = new ClientSession { Id = "recent", Name = "recent", LastPoll = now.AddMinutes(-1) };
        dict["stale"] = new ClientSession { Id = "stale", Name = "stale", LastPoll = now.AddHours(-2) };

        var pruned = ClientRegistry.PruneStale(dict, TimeSpan.FromHours(1), now);

        Assert.Equal(1, pruned);
        Assert.True(dict.ContainsKey("recent"));
        Assert.False(dict.ContainsKey("stale"));
    }

    [Fact]
    public void PruneStale_SkipsSessionsThatNeverPolled()
    {
        var now = DateTime.UtcNow;
        var dict = new ConcurrentDictionary<string, ClientSession>();
        dict["fresh"] = new ClientSession { Id = "fresh", Name = "fresh" }; // LastPoll == MinValue

        var pruned = ClientRegistry.PruneStale(dict, TimeSpan.FromMinutes(1), now);

        Assert.Equal(0, pruned);
        Assert.True(dict.ContainsKey("fresh"));
    }

    [Fact]
    public void PruneStale_AtBoundaryThreshold_Retains()
    {
        var now = DateTime.UtcNow;
        var dict = new ConcurrentDictionary<string, ClientSession>();
        dict["edge"] = new ClientSession { Id = "edge", Name = "edge", LastPoll = now.AddMinutes(-60) };

        var pruned = ClientRegistry.PruneStale(dict, TimeSpan.FromMinutes(60), now);

        Assert.Equal(0, pruned);
        Assert.True(dict.ContainsKey("edge"));
    }

    [Fact]
    public async Task PruneStale_CompletesPendingTaskCompletionSources()
    {
        var now = DateTime.UtcNow;
        var dict = new ConcurrentDictionary<string, ClientSession>();
        var pending = new PendingCommand { Command = "stuck-cmd" };
        var session = new ClientSession
        {
            Id = "stuck",
            Name = "stuck",
            LastPoll = now.AddHours(-5),
            UploadTcs = new TaskCompletionSource<bool>(),
            DownloadTcs = new TaskCompletionSource<FileTransfer>(),
        };
        session.InFlight[pending.RequestId] = pending;
        dict["stuck"] = session;

        ClientRegistry.PruneStale(dict, TimeSpan.FromHours(1), now);

        Assert.True(pending.Tcs.Task.IsCompleted);
        Assert.Equal(-1, (await pending.Tcs.Task).ExitCode);
        Assert.True(session.UploadTcs.Task.IsCompleted);
        Assert.False(await session.UploadTcs.Task);
        Assert.True(session.DownloadTcs.Task.IsCompleted);
        Assert.Equal("Session pruned", (await session.DownloadTcs.Task).Error);
    }

    [Fact]
    public void PruneStale_MultipleStale_PrunesAll()
    {
        var now = DateTime.UtcNow;
        var dict = new ConcurrentDictionary<string, ClientSession>();
        for (var i = 0; i < 10; i++)
        {
            dict[$"s{i}"] = new ClientSession { Id = $"s{i}", Name = $"s{i}", LastPoll = now.AddHours(-2) };
        }

        var pruned = ClientRegistry.PruneStale(dict, TimeSpan.FromHours(1), now);

        Assert.Equal(10, pruned);
        Assert.Empty(dict);
    }
}
