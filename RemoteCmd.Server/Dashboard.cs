using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using RemoteCmd.Shared;

/// <summary>
/// Live "top"-style status view for the relay console: which clients are connected, for how long,
/// how many commands are running/queued on each, completed count, and file-transfer state.
/// <see cref="Render"/> is a pure function (unit-testable); the loop in Program.cs just repaints it.
/// </summary>
public static class Dashboard
{
    public static string Render(
        ConcurrentDictionary<string, ClientSession> clients,
        int port,
        bool noTls,
        DateTime startedUtc,
        DateTime nowUtc,
        IReadOnlyList<string>? events = null,
        int height = 0)
    {
        var sessions = clients.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var connected = sessions.Count(c => c.IsConnected());
        var running = sessions.Sum(c => c.InFlight.Count);
        var queued = sessions.Sum(c => c.CommandQueue.Count);

        var rows = new List<string>
        {
            $"=== Remote CMD Relay :{port} {(noTls ? "http" : "https")} — up {Fmt(nowUtc - startedUtc)} ===",
            $"Clients: {connected} connected / {sessions.Count} total   " +
            $"Commands: {running} running, {queued} queued   {nowUtc.ToLocalTime():HH:mm:ss}",
            "",
            Row("NAME", "ID", "ADDRESS", "TOKEN", "POLL", "RUN", "QUEUE", "DONE", "STATE"),
        };

        if (sessions.Count == 0)
            rows.Add("(no clients registered)");

        foreach (var c in sessions)
        {
            var age = c.LastPoll == DateTime.MinValue ? "-" : (int)(nowUtc - c.LastPoll).TotalSeconds + "s";
            var poll = c.IsConnected() ? age : age + "!"; // '!' = past the 10s connected window
            var state = c.PendingUpload != null ? "UPLOAD"
                      : c.PendingDownload != null ? "DOWNLOAD"
                      : c.IsConnected() ? "idle" : "STALE";
            rows.Add(Row(
                Trunc(c.Name, 17),
                c.Id.Length <= 8 ? c.Id : c.Id[..8],
                Trunc(string.IsNullOrEmpty(c.RemoteIp) ? "-" : c.RemoteIp, 15),
                Trunc(c.TokenLabel, 9),
                poll,
                c.InFlight.Count.ToString(),
                c.CommandQueue.Count.ToString(),
                Interlocked.Read(ref c.CommandsServed).ToString(),
                state));
        }

        if (events is { Count: > 0 })
        {
            rows.Add("");
            rows.Add("--- recent events ---");
            var room = height > 0 ? Math.Max(1, height - rows.Count - 1) : events.Count;
            foreach (var e in events.TakeLast(room))
                rows.Add(e);
        }

        if (height > 0 && rows.Count > height)
            rows = rows.Take(height).ToList();

        return TerminalScreen.Frame(rows);
    }

    private static string Row(string name, string id, string address, string token, string poll, string run, string queue, string done, string state)
        => string.Format("{0,-18}{1,-10}{2,-17}{3,-11}{4,-8}{5,-6}{6,-7}{7,-7}{8}",
                         name, id, address, token, poll, run, queue, done, state);

    private static string Fmt(TimeSpan t)
        => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    private static string Trunc(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "~";

    /// <summary>Enter the alternate screen buffer so repaints stay in place instead of filling scrollback.</summary>
    public static void Enter() => TerminalScreen.Enter();

    /// <summary>Restore the terminal to the state it had before the dashboard took over.</summary>
    public static void Leave() => TerminalScreen.Leave();

    /// <summary>Enable ANSI/VT processing on the Windows console. No-op elsewhere or when redirected.</summary>
    public static void EnableAnsi()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            var handle = GetStdHandle(-11); // STD_OUTPUT_HANDLE
            if (GetConsoleMode(handle, out var mode))
                SetConsoleMode(handle, mode | 0x0004); // ENABLE_VIRTUAL_TERMINAL_PROCESSING
        }
        catch { /* best-effort: redirected output has no console mode */ }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);
}
