using System.Collections.Concurrent;
using System.Security.Cryptography;

/// <summary>
/// Guards the dashboard listener. It is bound to loopback, so only this machine reaches it — but a
/// browser on this machine also runs pages from anywhere, and those must not drive the relay:
/// <list type="bullet">
/// <item>A Host header other than a loopback name means DNS rebinding (an outside page whose name now
/// resolves to 127.0.0.1); such requests are refused.</item>
/// <item>Anything that changes state needs the <see cref="CsrfHeader"/> header. A cross-site page cannot
/// add a custom header without a CORS preflight, which this listener never grants.</item>
/// <item>With a password set, a session cookie is required too (HttpOnly, SameSite=Strict).</item>
/// </list>
/// </summary>
public sealed class AdminGate
{
    public const string CsrfHeader = "X-RemoteCmd";
    public const string CookieName = "rcmd_admin";
    public static readonly TimeSpan SessionLife = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<string, DateTime> _sessions = new(StringComparer.Ordinal);

    public static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        // Strip the port; an IPv6 literal keeps its brackets.
        var name = host.StartsWith('[') ? host[..(host.IndexOf(']') + 1)] : host.Split(':')[0];
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
               || name == "127.0.0.1"
               || name == "[::1]";
    }

    public static bool IsStateChanging(string method)
        => !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method);

    public string StartSession(DateTime nowUtc)
    {
        foreach (var (id, expires) in _sessions)
            if (expires <= nowUtc) _sessions.TryRemove(id, out _);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = nowUtc + SessionLife;
        return token;
    }

    public bool IsValid(string? session, DateTime nowUtc)
        => session != null && _sessions.TryGetValue(session, out var expires) && expires > nowUtc;

    public void End(string? session)
    {
        if (session != null) _sessions.TryRemove(session, out _);
    }

    /// <summary>Signs everyone out, e.g. when the password changes.</summary>
    public void EndAll() => _sessions.Clear();
}
