using System.Net;

/// <summary>
/// Who may read the status endpoints without presenting a token.
/// </summary>
public static class OpenStatus
{
    /// <summary>
    /// <c>--open-status</c> exists so the dashboard just opens, and its justification is that
    /// "whoever reaches the port is already on this machine". That is only true of the loopback
    /// interface, so the anonymous view is limited to it — the relay itself keeps listening on every
    /// interface, because clients poll it across the network and they authenticate with a token.
    ///
    /// Restricting the listener instead would have taken every client offline along with the
    /// anonymous view: the bind covers the whole server, not just /ui.
    /// </summary>
    public static bool AllowsAnonymous(bool openStatus, bool readOnlyPath, string? presentedToken, IPAddress? peer)
        => openStatus
           && readOnlyPath
           && string.IsNullOrEmpty(presentedToken)
           && IsLocal(peer);

    /// <summary>
    /// Loopback, including the IPv4-mapped form Kestrel reports as ::ffff:127.0.0.1 when it accepts
    /// a v4 connection on a dual-stack socket.
    /// </summary>
    public static bool IsLocal(IPAddress? peer)
    {
        if (peer is null) return false;
        if (IPAddress.IsLoopback(peer)) return true;
        return peer.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(peer.MapToIPv4());
    }
}
