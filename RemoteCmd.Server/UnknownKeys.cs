using System.Collections.Concurrent;

/// <summary>A machine that keeps polling with a key the relay does not know.</summary>
public sealed record UnknownKeyMachine(string Peer, string Name, string Host, string Path, int Attempts, DateTime FirstUtc, DateTime LastUtc);

/// <summary>
/// Remembers who knocks with an unknown key on the client endpoints, by address and the name the
/// client reports. A machine whose old client outlived a re-pairing, or whose key was revoked, then
/// shows up on the dashboard as a machine to pair again rather than as an anonymous 401.
/// At most 64 entries, each forgotten 30 minutes after its last attempt.
/// </summary>
public sealed class UnknownKeys
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);
    private const int MaxEntries = 64;

    private readonly ConcurrentDictionary<string, UnknownKeyMachine> _seen = new(StringComparer.Ordinal);

    /// <param name="name">What the client calls itself; anyone can send anything here, so it is cleaned.</param>
    public void Record(string peer, string? name, string? host, string path, DateTime nowUtc)
    {
        var cleanName = Clean(name);
        var cleanHost = Clean(host);
        _seen.AddOrUpdate($"{peer}|{cleanName}",
            _ => new UnknownKeyMachine(peer, cleanName, cleanHost, path, 1, nowUtc, nowUtc),
            (_, old) => old with { Attempts = old.Attempts + 1, LastUtc = nowUtc, Path = path, Host = cleanHost.Length > 0 ? cleanHost : old.Host });
        if (_seen.Count > MaxEntries) Prune(nowUtc, keep: MaxEntries / 2);
    }

    /// <summary>Machines seen within <see cref="Window"/>, most recent first.</summary>
    public IReadOnlyList<UnknownKeyMachine> Recent(DateTime nowUtc)
    {
        Prune(nowUtc, keep: MaxEntries);
        return _seen.Values.OrderByDescending(m => m.LastUtc).ToList();
    }

    /// <summary>Forgets a machine, e.g. once it has been paired again.</summary>
    public void Forget(string peer, string name) => _seen.TryRemove($"{peer}|{name}", out _);

    private void Prune(DateTime nowUtc, int keep)
    {
        foreach (var (key, m) in _seen)
            if (nowUtc - m.LastUtc > Window) _seen.TryRemove(key, out _);
        foreach (var (key, _) in _seen.OrderByDescending(kv => kv.Value.LastUtc).Skip(keep).ToList())
            _seen.TryRemove(key, out _);
    }

    /// <summary>Printable and short: the dashboard shows it, and it came from the network.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return new string(text.Where(c => !char.IsControl(c)).Take(64).ToArray()).Trim();
    }
}
