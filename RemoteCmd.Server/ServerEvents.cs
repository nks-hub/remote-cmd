using System.Collections.Concurrent;

/// <summary>One line of relay history: a connect, a command, a transfer or a rejected request.</summary>
public sealed record ServerEvent(DateTime AtUtc, string Kind, string Client, string Message)
{
    public string ToLine() => $"{AtUtc.ToLocalTime():HH:mm:ss} {Kind,-8} {Client,-14} {Message}";
}

/// <summary>
/// Bounded in-memory history of relay activity. Feeds both the console dashboard and the web
/// status page; nothing is persisted, so it costs a fixed amount of memory and survives nothing.
/// </summary>
public sealed class EventLog
{
    private readonly int _capacity;
    private readonly ConcurrentQueue<ServerEvent> _events = new();
    private int _count;

    public EventLog(int capacity = 500) => _capacity = capacity;

    public void Add(string kind, string client, string message)
    {
        _events.Enqueue(new ServerEvent(DateTime.UtcNow, kind, client, message));
        Interlocked.Increment(ref _count);
        while (Volatile.Read(ref _count) > _capacity && _events.TryDequeue(out _))
            Interlocked.Decrement(ref _count);
    }

    public IReadOnlyList<ServerEvent> Snapshot() => _events.ToArray();

    public IReadOnlyList<string> Lines() => _events.Select(e => e.ToLine()).ToArray();
}

/// <summary>Counters shown on the status page. Plain fields so Interlocked can bump them.</summary>
public sealed class RelayStats
{
    public long Execs;
    public long Uploads;
    public long Downloads;
    public long BytesUploaded;
    public long BytesDownloaded;
    public long AuthFailures;
    public long Timeouts;

    public object Snapshot() => new
    {
        execs = Interlocked.Read(ref Execs),
        uploads = Interlocked.Read(ref Uploads),
        downloads = Interlocked.Read(ref Downloads),
        bytesUploaded = Interlocked.Read(ref BytesUploaded),
        bytesDownloaded = Interlocked.Read(ref BytesDownloaded),
        authFailures = Interlocked.Read(ref AuthFailures),
        timeouts = Interlocked.Read(ref Timeouts),
    };
}
