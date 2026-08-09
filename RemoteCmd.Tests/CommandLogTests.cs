using Xunit;

namespace RemoteCmd.Tests;

/// <summary>
/// The command store keeps output in memory only, so it has to stay bounded in both directions:
/// a fixed number of records, each of a fixed maximum size, whoever is writing to it.
/// </summary>
public class CommandLogTests
{
    private static void Add(CommandLog log, string id, string output = "out", string command = "cmd")
        => log.Add(id, DateTime.UtcNow, "box", command, output, 0, 12);

    [Fact]
    public void StoresWhatTheCommandProduced()
    {
        var log = new CommandLog();

        log.Add("abc", new DateTime(2026, 8, 9, 10, 0, 0, DateTimeKind.Utc), "macm1", "echo hi", "hi", 0, 42);

        var record = log.Get("abc");
        Assert.NotNull(record);
        Assert.Equal("macm1", record!.Client);
        Assert.Equal("echo hi", record.Command);
        Assert.Equal("hi", record.Stdout);
        Assert.Equal(0, record.ExitCode);
        Assert.Equal(42, record.DurationMs);
        Assert.False(record.Truncated);
    }

    [Fact]
    public void UnknownIdHasNoRecord() => Assert.Null(new CommandLog().Get("nope"));

    [Fact]
    public void SplitsTheStderrHalfOfTheClientsCombinedOutput()
    {
        var log = new CommandLog();

        Add(log, "id", "line one\nline two\n[STDERR]\nboom");

        var record = log.Get("id")!;
        Assert.Equal("line one\nline two", record.Stdout);
        Assert.Equal("boom", record.Stderr);
    }

    [Fact]
    public void OutputWithoutTheMarkerIsAllStdout()
    {
        var log = new CommandLog();

        Add(log, "id", "just stdout");

        Assert.Equal("just stdout", log.Get("id")!.Stdout);
        Assert.Equal("", log.Get("id")!.Stderr);
    }

    [Fact]
    public void LongOutputIsCutAndFlagged()
    {
        var log = new CommandLog();

        Add(log, "id", new string('x', CommandLog.MaxTextLength + 5_000));

        var record = log.Get("id")!;
        Assert.Equal(CommandLog.MaxTextLength, record.Stdout.Length);
        Assert.True(record.Truncated);
    }

    [Fact]
    public void LongCommandIsCutAndFlagged()
    {
        var log = new CommandLog();

        Add(log, "id", "out", new string('c', CommandLog.MaxTextLength + 1));

        var record = log.Get("id")!;
        Assert.Equal(CommandLog.MaxTextLength, record.Command.Length);
        Assert.True(record.Truncated);
    }

    [Fact]
    public void OldestRecordsAreDroppedOnceTheCapIsReached()
    {
        var log = new CommandLog(capacity: 3);

        for (var i = 0; i < 5; i++) Add(log, $"id-{i}");

        Assert.Equal(3, log.Count);
        Assert.Null(log.Get("id-0"));
        Assert.Null(log.Get("id-1"));
        Assert.NotNull(log.Get("id-4"));
    }

    [Fact]
    public void ARepeatedIdDoesNotDisturbEviction()
    {
        var log = new CommandLog(capacity: 2);

        Add(log, "same");
        Add(log, "same");
        Add(log, "other");

        Assert.Equal(2, log.Count);
        Assert.NotNull(log.Get("same"));
        Assert.NotNull(log.Get("other"));
    }

    [Fact]
    public void ConcurrentWritersFillTheCapExactly_AndNeverExceedIt()
    {
        var log = new CommandLog(capacity: 50);

        Parallel.For(0, 2_000, i => Add(log, $"id-{i}"));

        // Exactly full, not merely "not over": writers racing on the same overflow used to each
        // evict for it, so a burst could empty a log that was only one entry past capacity.
        var alive = Enumerable.Range(0, 2_000).Count(i => log.Get($"id-{i}") != null);
        Assert.Equal(50, log.Count);
        Assert.Equal(50, alive);
    }

    [Fact]
    public void ASingleSlotLogKeepsExactlyOneRecordUnderContention()
    {
        var log = new CommandLog(capacity: 1);

        Parallel.For(0, 500, i => Add(log, $"id-{i}"));

        Assert.Equal(1, log.Count);
        Assert.Equal(1, Enumerable.Range(0, 500).Count(i => log.Get($"id-{i}") != null));
    }

    [Fact]
    public void TheClientNameIsCappedToo()
    {
        var log = new CommandLog();

        log.Add("id", DateTime.UtcNow, new string('n', 10_000), "cmd", "out", 0, 1);

        Assert.Equal(256, log.Get("id")!.Client.Length);
    }
}
