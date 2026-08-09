namespace RemoteCmd.Shared;

/// <summary>
/// How long a single command may run. The relay resolves the caller's request against these limits
/// and hands the agreed number to the client with the command, so both ends stop at the same moment
/// instead of the client applying a fixed limit of its own.
/// </summary>
public static class ExecLimits
{
    /// <summary>Applied when the caller asks for nothing in particular.</summary>
    public const int DefaultSeconds = 60;

    /// <summary>Hard ceiling, so a caller cannot park a process on a machine indefinitely.</summary>
    public const int MaxSeconds = 3600;

    /// <summary>
    /// Extra time the relay waits on top of the command's own limit, so a client that stops right
    /// on the deadline still gets its (killed) output back instead of racing the relay's timeout.
    /// </summary>
    public const int RelayGraceSeconds = 5;

    /// <summary>Resolves a requested timeout; anything at or below zero means "use the default".</summary>
    public static int Clamp(int requestedSeconds)
        => requestedSeconds <= 0 ? DefaultSeconds : Math.Min(requestedSeconds, MaxSeconds);
}
