namespace RemoteCmd.Client;

/// <summary>
/// Live counters for the client dashboard. Mutated from the poll loop and the
/// per-command tasks; read by the dashboard repaint loop. All access is via
/// Interlocked / volatile so no lock is needed for the simple scalar fields.
/// </summary>
public sealed class ClientStats
{
    public required string ServerUrl { get; init; }
    public required string Name { get; init; }
    public required string ClientId { get; init; }
    public required string TokenMasked { get; init; }
    public required string Shell { get; init; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    private long _polls;
    private long _running;
    private long _served;
    private long _lastPollTicks;      // DateTime.UtcNow.Ticks of the last successful poll
    private volatile string _lastLine = "starting…";

    public long Polls => Interlocked.Read(ref _polls);
    public long Running => Interlocked.Read(ref _running);
    public long Served => Interlocked.Read(ref _served);
    public string LastLine => _lastLine;

    public DateTime LastPollUtc
    {
        get { var t = Interlocked.Read(ref _lastPollTicks); return t == 0 ? DateTime.MinValue : new DateTime(t, DateTimeKind.Utc); }
    }

    /// <summary>Connected == a successful poll within the last 15s (a couple of idle cadences).</summary>
    public bool Connected => LastPollUtc != DateTime.MinValue && (DateTime.UtcNow - LastPollUtc).TotalSeconds < 15;

    public void MarkPoll() { Interlocked.Increment(ref _polls); Interlocked.Exchange(ref _lastPollTicks, DateTime.UtcNow.Ticks); }
    public void ExecStarted() => Interlocked.Increment(ref _running);
    public void ExecFinished() { Interlocked.Decrement(ref _running); Interlocked.Increment(ref _served); }
    public void SetLastLine(string line) => _lastLine = line;

    /// <summary>Assembly version stamped by the build (`-p:Version` in CI); "dev" for a plain local build.</summary>
    public static string Version => RemoteCmd.Shared.VersionInfo.Version;
}
