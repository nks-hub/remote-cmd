using System.Collections.Concurrent;

/// <summary>A machine that keeps polling with a key the relay does not know.</summary>
public sealed record UnknownKeyMachine(string Peer, string Name, string Host, string Path, int Attempts, DateTime FirstUtc, DateTime LastUtc);

/// <summary>
/// Remembers who knocks with an unknown key on the client endpoints, by address and the name the
/// client reports. A machine whose old client outlived a re-pairing, or whose key was revoked, then
/// shows up on the dashboard as a machine to pair again rather than as an anonymous 401.
///
/// Anyone on the network can send this, so it is kept small and plain: one entry per address
/// (a sender rotating names only overwrites its own), at most 64 addresses, each forgotten 30
/// minutes after its last attempt, and names reduced to the characters a host name uses.
/// </summary>
public sealed class UnknownKeys
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);
    private const int MaxEntries = 64;

    private readonly ConcurrentDictionary<string, UnknownKeyMachine> _seen = new(StringComparer.Ordinal);

    public void Record(string peer, string? name, string? host, string path, DateTime nowUtc)
    {
        var cleanName = Clean(name);
        var cleanHost = Clean(host);
        _seen.AddOrUpdate(peer,
            _ => new UnknownKeyMachine(peer, cleanName, cleanHost, path, 1, nowUtc, nowUtc),
            (_, old) => old with { Name = cleanName, Host = cleanHost, Path = path, Attempts = old.Attempts + 1, LastUtc = nowUtc });
        if (_seen.Count > MaxEntries) Prune(nowUtc, keep: MaxEntries);
    }

    /// <summary>Machines seen within <see cref="Window"/>, most recent first.</summary>
    public IReadOnlyList<UnknownKeyMachine> Recent(DateTime nowUtc)
    {
        Prune(nowUtc, keep: MaxEntries);
        return _seen.Values.OrderByDescending(m => m.LastUtc).ToList();
    }

    /// <summary>Forgets an address once a machine there has been paired again.</summary>
    public void ForgetPeer(string peer) => _seen.TryRemove(peer, out _);

    private void Prune(DateTime nowUtc, int keep)
    {
        foreach (var (key, m) in _seen)
            if (nowUtc - m.LastUtc > Window) _seen.TryRemove(key, out _);
        foreach (var (key, _) in _seen.OrderByDescending(kv => kv.Value.LastUtc).Skip(keep).ToList())
            _seen.TryRemove(key, out _);
    }

    /// <summary>
    /// Letters, digits and the punctuation of host names, at most 64 of them. Invisible and
    /// direction-changing characters would otherwise let a sender make its name read as something else.
    /// </summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return new string(text.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or '-' or '_')
            .Take(64).ToArray()).Trim();
    }
}
