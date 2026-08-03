using System.Collections.Concurrent;

/// <summary>
/// Slows down token guessing. The relay is usually reachable from the internet and a valid token is
/// remote code execution, so an unlimited guess rate is worth closing: after <see cref="MaxFailures"/>
/// bad tokens within a minute, further *rejected* requests from that address are refused outright for
/// <see cref="LockoutMinutes"/> minutes. Requests carrying a valid token are never consulted here, so
/// an attacker behind the same NAT address as a real client cannot lock that client out.
/// </summary>
public sealed class AuthThrottle
{
    public const int MaxFailures = 10;
    public const int LockoutMinutes = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private sealed class Entry
    {
        public int Failures;
        public DateTime FirstFailureUtc;
        public DateTime LockedUntilUtc;
    }

    private readonly ConcurrentDictionary<string, Entry> _peers = new(StringComparer.Ordinal);

    public bool IsLockedOut(string peer, DateTime nowUtc)
        => _peers.TryGetValue(peer, out var e) && e.LockedUntilUtc > nowUtc;

    /// <summary>Count a rejected request. Returns true when this failure triggered the lockout.</summary>
    public bool RecordFailure(string peer, DateTime nowUtc)
    {
        var entry = _peers.GetOrAdd(peer, _ => new Entry { FirstFailureUtc = nowUtc });
        lock (entry)
        {
            // Failures older than the window don't count towards the lockout.
            if (nowUtc - entry.FirstFailureUtc > Window)
            {
                entry.Failures = 0;
                entry.FirstFailureUtc = nowUtc;
            }

            entry.Failures++;
            if (entry.Failures < MaxFailures) return false;

            entry.LockedUntilUtc = nowUtc.AddMinutes(LockoutMinutes);
            entry.Failures = 0;
            entry.FirstFailureUtc = nowUtc;
            return true;
        }
    }

    /// <summary>Drop entries that are neither locked out nor recently active.</summary>
    public int Prune(DateTime nowUtc)
    {
        var removed = 0;
        foreach (var (peer, entry) in _peers)
        {
            if (entry.LockedUntilUtc > nowUtc) continue;
            if (nowUtc - entry.FirstFailureUtc <= Window) continue;
            if (_peers.TryRemove(peer, out _)) removed++;
        }
        return removed;
    }
}
