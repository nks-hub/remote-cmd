namespace RemoteCmd.Shared;

/// <summary>
/// How the relay asks a client for a picture of its screen. The request travels as an ordinary
/// command so no new transport, queue or poll endpoint is needed; the client recognises the prefix
/// and captures instead of running a shell. A client too old to know the prefix simply tries to run
/// it and fails, which the relay reports as a failed screenshot rather than a silent blank.
/// </summary>
public static class ScreenshotProtocol
{
    /// <summary>
    /// Marks a command as a capture request. Anything after it is an optional destination path;
    /// with none, the client writes to its temp directory and prunes its own older captures.
    /// </summary>
    public const string CommandPrefix = "__screenshot__";

    /// <summary>Response header carrying the client's size note and any blank-screen warning.</summary>
    public const string NoteHeader = "X-Screenshot-Note";
}
