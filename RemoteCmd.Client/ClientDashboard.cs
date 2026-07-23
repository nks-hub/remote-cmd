using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace RemoteCmd.Client;

/// <summary>
/// A "top"-style client console: a fixed stats header at the top and a live,
/// scrolling log underneath. <see cref="Render"/> is a pure function of the
/// stats plus the log ring, so it is unit-testable; the loop in Program.cs
/// just repaints its output on a timer.
/// </summary>
public static class ClientDashboard
{
    private const string HomeAndClear = "\x1b[H\x1b[2J"; // cursor home + clear screen

    public static string Render(ClientStats s, IReadOnlyList<string> log, int width, int height, DateTime nowUtc)
    {
        if (width < 20) width = 20;
        if (height < 6) height = 6;

        var sb = new StringBuilder();
        sb.Append(HomeAndClear);

        // --- Static header (stats) ---
        var state = s.Connected ? "CONNECTED" : "connecting…";
        var age = s.LastPollUtc == DateTime.MinValue ? "-" : (int)(nowUtc - s.LastPollUtc).TotalSeconds + "s";
        Line(sb, $"RemoteCmd Client v{ClientStats.Version}   {state}   up {Fmt(nowUtc - s.StartedUtc)}", width);
        Line(sb, $"server {s.ServerUrl}   token {s.TokenMasked}   shell {s.Shell}", width);
        Line(sb, $"name {s.Name}   id {(s.ClientId.Length <= 8 ? s.ClientId : s.ClientId[..8])}   {nowUtc.ToLocalTime():HH:mm:ss}", width);
        Line(sb, $"polls {s.Polls}   last poll {age}   running {s.Running}   served {s.Served}", width);
        sb.Append(new string('─', width)).Append('\n');

        // --- Live log (last N lines that fit) ---
        var logRows = height - 6; // 4 header + separator + 1 spare
        if (logRows < 1) logRows = 1;
        var start = Math.Max(0, log.Count - logRows);
        for (var i = start; i < log.Count; i++)
            Line(sb, log[i], width);

        return sb.ToString();
    }

    // Clip to the console width and clear to end-of-line so stale characters from a longer
    // previous frame don't linger (we clear the whole screen each frame, but this is belt-and-braces).
    private static void Line(StringBuilder sb, string text, int width)
    {
        if (text.Length > width) text = text[..(width - 1)] + "…";
        sb.Append(text).Append('\n');
    }

    private static string Fmt(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    /// <summary>Enable ANSI/VT processing on the Windows console. No-op elsewhere or when redirected.</summary>
    public static void EnableAnsi()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            var handle = GetStdHandle(-11);
            if (GetConsoleMode(handle, out var mode))
                SetConsoleMode(handle, mode | 0x0004); // ENABLE_VIRTUAL_TERMINAL_PROCESSING
        }
        catch { /* redirected output has no console mode */ }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(nint h, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(nint h, uint mode);
}

/// <summary>Bounded, thread-safe ring of the most recent log lines shown under the header.</summary>
public sealed class LogRing
{
    private readonly int _capacity;
    private readonly ConcurrentQueue<string> _lines = new();
    private int _count;

    public LogRing(int capacity = 500) => _capacity = capacity;

    public void Add(string line)
    {
        foreach (var l in line.Split('\n'))
        {
            _lines.Enqueue(l.TrimEnd('\r'));
            Interlocked.Increment(ref _count);
        }
        while (Volatile.Read(ref _count) > _capacity && _lines.TryDequeue(out _))
            Interlocked.Decrement(ref _count);
    }

    public IReadOnlyList<string> Snapshot() => _lines.ToArray();
}
