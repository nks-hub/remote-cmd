using RemoteCmd.Client;
using Xunit;

namespace RemoteCmd.Tests;

public class ClientDashboardTests
{
    private static ClientStats MakeStats() => new()
    {
        ServerUrl = "http://relay:7890",
        Name = "macm1",
        ClientId = "abcdef1234567890",
        TokenMasked = "he****23",
        Shell = "/bin/bash",
    };

    [Fact]
    public void Render_HeaderShowsStatsAndScrollingLog()
    {
        var stats = MakeStats();
        stats.MarkPoll();
        stats.ExecStarted(); // 1 running

        var log = new[] { "10:00:01 info Remote CMD Client started", "10:00:02 info [CMD] whoami" };
        var text = ClientDashboard.Render(stats, log, width: 100, height: 20, nowUtc: DateTime.UtcNow);

        // Static header
        Assert.Contains("RemoteCmd Client v", text);
        Assert.Contains("CONNECTED", text);
        Assert.Contains("http://relay:7890", text);
        Assert.Contains("he****23", text);
        Assert.Contains("macm1", text);
        Assert.Contains("running 1", text);
        Assert.Contains("polls 1", text);
        // Log underneath
        Assert.Contains("[CMD] whoami", text);
    }

    [Fact]
    public void Render_ClampsLogToConsoleHeight()
    {
        var stats = MakeStats();
        var log = Enumerable.Range(0, 100).Select(i => $"line-{i}").ToArray();

        // height 12 → 6 rows of log; must show the LAST lines, not the first.
        var text = ClientDashboard.Render(stats, log, width: 80, height: 12, nowUtc: DateTime.UtcNow);

        Assert.Contains("line-99", text);
        // "line-0\n" could never appear even in a broken render — the lines are padded and end with
        // an escape sequence, so the assertion passed no matter what was drawn. Check the line that
        // must have been dropped instead, with a word boundary so "line-0" does not match "line-90".
        Assert.DoesNotMatch(@"line-0\b", text);
        Assert.DoesNotMatch(@"line-50\b", text);
    }

    [Fact]
    public void LogRing_KeepsOnlyMostRecentUpToCapacity()
    {
        var ring = new LogRing(capacity: 3);
        ring.Add("a");
        ring.Add("b");
        ring.Add("c");
        ring.Add("d"); // evicts "a"

        var snap = ring.Snapshot();
        Assert.Equal(3, snap.Count);
        Assert.Equal(new[] { "b", "c", "d" }, snap);
    }

    [Fact]
    public void LogRing_SplitsMultilineInput()
    {
        var ring = new LogRing(capacity: 10);
        ring.Add("first\nsecond\nthird");

        Assert.Equal(new[] { "first", "second", "third" }, ring.Snapshot());
    }

    [Fact]
    public void Stats_ConnectedFalseUntilFirstPoll()
    {
        var stats = MakeStats();
        Assert.False(stats.Connected);
        stats.MarkPoll();
        Assert.True(stats.Connected);
    }
}
