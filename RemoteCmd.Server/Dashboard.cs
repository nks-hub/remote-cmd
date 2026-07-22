using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Live "top"-style status view for the relay console: which clients are connected, for how long,
/// how many commands are running/queued on each, completed count, and file-transfer state.
/// <see cref="Render"/> is a pure function (unit-testable); the loop in Program.cs just repaints it.
/// </summary>
public static class Dashboard
{
    private const string HomeAndClear = "\x1b[H\x1b[2J"; // cursor home + clear screen

    public static string Render(
        ConcurrentDictionary<string, ClientSession> clients,
        int port,
        bool noTls,
        DateTime startedUtc,
        DateTime nowUtc)
    {
        var sessions = clients.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var connected = sessions.Count(c => c.IsConnected());
        var running = sessions.Sum(c => c.InFlight.Count);
        var queued = sessions.Sum(c => c.CommandQueue.Count);

        var sb = new StringBuilder();
        sb.Append(HomeAndClear);
        sb.Append("=== Remote CMD Relay :").Append(port).Append(' ').Append(noTls ? "http" : "https")
          .Append(" — up ").Append(Fmt(nowUtc - startedUtc)).Append(" ===\n");
        sb.Append("Clients: ").Append(connected).Append(" connected / ").Append(sessions.Count).Append(" total")
          .Append("   Commands: ").Append(running).Append(" running, ").Append(queued).Append(" queued")
          .Append("   ").Append(nowUtc.ToLocalTime().ToString("HH:mm:ss")).Append('\n');
        sb.Append('\n');
        sb.Append(Row("NAME", "ID", "POLL", "RUN", "QUEUE", "DONE", "STATE"));

        if (sessions.Count == 0)
            sb.Append("(no clients registered)\n");

        foreach (var c in sessions)
        {
            var age = c.LastPoll == DateTime.MinValue ? "-" : (int)(nowUtc - c.LastPoll).TotalSeconds + "s";
            var poll = c.IsConnected() ? age : age + "!"; // '!' = past the 10s connected window
            var state = c.PendingUpload != null ? "UPLOAD"
                      : c.PendingDownload != null ? "DOWNLOAD"
                      : c.IsConnected() ? "idle" : "STALE";
            sb.Append(Row(
                Trunc(c.Name, 17),
                c.Id.Length <= 8 ? c.Id : c.Id[..8],
                poll,
                c.InFlight.Count.ToString(),
                c.CommandQueue.Count.ToString(),
                Interlocked.Read(ref c.CommandsServed).ToString(),
                state));
        }
        return sb.ToString();
    }

    private static string Row(string name, string id, string poll, string run, string queue, string done, string state)
        => string.Format("{0,-18}{1,-10}{2,-8}{3,-6}{4,-7}{5,-7}{6}\n", name, id, poll, run, queue, done, state);

    private static string Fmt(TimeSpan t)
        => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    private static string Trunc(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "~";

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
