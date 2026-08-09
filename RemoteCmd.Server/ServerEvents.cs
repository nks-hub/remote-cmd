using System.Collections.Concurrent;

/// <summary>
/// One line of relay history: a connect, a command, a transfer or a rejected request.
/// <paramref name="Id"/> is the request id of the command the line belongs to, when there is one,
/// so the status page can pull that command's output.
/// </summary>
public sealed record ServerEvent(DateTime AtUtc, string Kind, string Client, string Message, string? Id = null)
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

    // Append-then-evict is two steps, and doing them without a lock let concurrent writers each
    // observe the same overflow and evict for it, emptying a log that was only one entry over.
    private readonly Lock _gate = new();

    public EventLog(int capacity = 500) => _capacity = capacity;

    public void Add(string kind, string client, string message, string? id = null)
    {
        lock (_gate)
        {
            _events.Enqueue(new ServerEvent(DateTime.UtcNow, kind, client, message, id));
            while (_events.Count > _capacity) _events.TryDequeue(out _);
        }
    }

    public IReadOnlyList<ServerEvent> Snapshot() => _events.ToArray();

    public IReadOnlyList<string> Lines() => _events.Select(e => e.ToLine()).ToArray();
}

/// <summary>What one command produced, as the status page shows it.</summary>
public sealed record CommandRecord(
    string Id,
    DateTime StartedUtc,
    string Client,
    string Command,
    string Stdout,
    string Stderr,
    int ExitCode,
    int DurationMs,
    bool Truncated);

/// <summary>
/// Bounded in-memory store of command output, keyed by the exec's request id. Same deal as
/// <see cref="EventLog"/>: nothing is written to disk and nothing survives a restart. Both the
/// number of records and the size of each one are capped, so a chatty command cannot grow the
/// relay's memory without limit — the worst case is capacity × 2 × <see cref="MaxTextLength"/>.
/// </summary>
public sealed class CommandLog
{
    /// <summary>Per-record cap for the command and for its output; longer text is cut and flagged.</summary>
    public const int MaxTextLength = 64 * 1024;

    // Every client marks the stderr half of its combined output with this line, so the two streams
    // can be shown apart without changing the wire protocol.
    private const string StderrMarker = "\n[STDERR]\n";

    private readonly int _capacity;
    private readonly ConcurrentDictionary<string, CommandRecord> _records = new();
    private readonly Queue<string> _order = new();

    // The dictionary, the eviction order and the count only make sense together, so every write
    // takes them as one step. Without it, two writers could both act on the same overflow and evict
    // twice for one insert, throwing away history that was still within capacity.
    private readonly Lock _gate = new();

    public CommandLog(int capacity = 200) => _capacity = capacity;

    public int Count => _records.Count;

    public void Add(string id, DateTime startedUtc, string client, string command, string output, int exitCode, int durationMs)
    {
        var (cmd, cmdCut) = Cap(command);
        var (text, outCut) = Cap(output);
        var (stdout, stderr) = SplitStreams(text);
        // The name is chosen by whoever started the client, so it is capped like everything else
        // that goes into a record — the store's size ceiling has to hold for every field.
        var record = new CommandRecord(id, startedUtc, Cap(client, 256).Text, cmd, stdout, stderr,
            exitCode, durationMs, cmdCut || outCut);

        lock (_gate)
        {
            // One record per exec: a repeated id must not add a second eviction slot, or an entry
            // would be dropped while another slot still pointed at it.
            if (!_records.TryAdd(id, record)) return;
            _order.Enqueue(id);
            while (_order.Count > _capacity) _records.TryRemove(_order.Dequeue(), out _);
        }
    }

    public CommandRecord? Get(string id) => _records.TryGetValue(id, out var r) ? r : null;

    private static (string Text, bool Truncated) Cap(string s, int limit = MaxTextLength)
        => s.Length <= limit ? (s, false) : (s[..limit], true);

    private static (string Stdout, string Stderr) SplitStreams(string output)
    {
        var i = output.IndexOf(StderrMarker, StringComparison.Ordinal);
        return i < 0 ? (output, "") : (output[..i], output[(i + StderrMarker.Length)..]);
    }
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
