/// <summary>
/// Which address Kestrel listens on.
/// </summary>
public static class RelayBinding
{
    /// <summary>
    /// With <c>--open-status</c> the relay hands the whole dashboard — including the text of every
    /// command and its stored stdout — to anyone who asks, with no token at all. The justification
    /// for that is "whoever reaches the port is already on this machine", which is only true if the
    /// relay actually refuses everyone else: bound to 0.0.0.0 it was handing command output to the
    /// entire network. So the open mode listens on loopback, and only on loopback. Reach it from
    /// elsewhere over an SSH tunnel, which is an explicit decision rather than an accident.
    /// </summary>
    public static string UrlFor(bool openStatus, bool noTls, int port)
        => $"{(noTls ? "http" : "https")}://{(openStatus ? "127.0.0.1" : "0.0.0.0")}:{port}";

    /// <summary>Host part of <see cref="UrlFor"/>, for the startup banner.</summary>
    public static string HostFor(bool openStatus) => openStatus ? "127.0.0.1" : "0.0.0.0";
}
