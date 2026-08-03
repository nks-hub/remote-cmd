namespace RemoteCmd.Shared;

/// <summary>
/// Terminal control for "top"-style live views. Frames are drawn into the alternate screen buffer
/// and repainted in place: without it, clearing the screen every frame pushes the old frame into
/// the terminal's scrollback, so the history fills up with duplicate frames.
/// </summary>
public static class TerminalScreen
{
    public const string Home = "\x1b[H";          // cursor to top-left
    public const string ClearLineEnd = "\x1b[K";  // erase the rest of the current line
    public const string ClearBelow = "\x1b[J";    // erase everything below the cursor

    private const string AltScreenOn = "\x1b[?1049h";
    private const string AltScreenOff = "\x1b[?1049l";
    private const string CursorHide = "\x1b[?25l";
    private const string CursorShow = "\x1b[?25h";

    /// <summary>Switch to the alternate buffer and hide the cursor. No-op when output is redirected.</summary>
    public static void Enter()
    {
        if (Console.IsOutputRedirected) return;
        Write(AltScreenOn + CursorHide + Home + ClearBelow);
    }

    /// <summary>Restore the normal buffer and the cursor, leaving the scrollback as it was.</summary>
    public static void Leave()
    {
        if (Console.IsOutputRedirected) return;
        Write(CursorShow + AltScreenOff);
    }

    private static void Write(string s)
    {
        try
        {
            Console.Out.Write(s);
            Console.Out.Flush();
        }
        catch (IOException) { /* console went away — nothing to restore */ }
    }

    /// <summary>
    /// Join rows into one frame: cursor home, every row cleared to the end of the line, the area
    /// below the last row wiped. No trailing newline, which would scroll the view by one line.
    /// </summary>
    public static string Frame(IEnumerable<string> rows)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Home);
        var first = true;
        foreach (var row in rows)
        {
            if (!first) sb.Append('\n');
            sb.Append(row).Append(ClearLineEnd);
            first = false;
        }
        sb.Append(ClearBelow);
        return sb.ToString();
    }
}
