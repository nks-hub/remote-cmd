using System.Collections.Concurrent;
using RemoteCmd.Shared;

/// <summary>A pairing code waiting to be used.</summary>
public sealed class PendingPairing
{
    public string Id { get; init; } = "";
    public string Code { get; init; } = "";
    /// <summary>Name the paired machine gets; null lets the client propose its own.</summary>
    public string? Label { get; init; }
    /// <summary>Relay address the install commands for this code point at; null means the relay's own.</summary>
    public string? ServerUrl { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime ExpiresUtc { get; init; }
    public int Failures;
}

/// <summary>A code that was redeemed: which machine it became and from where.</summary>
public sealed record CompletedPairing(string Id, string Name, string CredentialId, string Peer, DateTime AtUtc);

/// <summary>
/// Pairing codes live only in memory: one that outlived a restart would be a code nobody is waiting
/// on any more. Each is single-use, expires, and is burnt after <see cref="MaxFailures"/> wrong proofs.
/// Redeemed codes are remembered for <see cref="RecentFor"/>, so whoever created one can watch the
/// machine arrive instead of guessing whether it worked.
/// </summary>
public sealed class PairingCodes
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaxTtl = TimeSpan.FromHours(24);
    public static readonly TimeSpan RecentFor = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, PendingPairing> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CompletedPairing> _completed = new(StringComparer.Ordinal);

    public PendingPairing Create(string? label, TimeSpan? ttl, DateTime nowUtc, string? serverUrl = null)
    {
        var life = ttl is { } t && t > TimeSpan.Zero ? (t > MaxTtl ? MaxTtl : t) : DefaultTtl;
        var code = Pairing.NewCode();
        var pending = new PendingPairing
        {
            Id = Pairing.CodeId(code),
            Code = code,
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            ServerUrl = string.IsNullOrWhiteSpace(serverUrl) ? null : serverUrl.Trim().TrimEnd('/'),
            CreatedUtc = nowUtc,
            ExpiresUtc = nowUtc + life,
        };
        _pending[pending.Id] = pending;
        return pending;
    }

    public PendingPairing? Find(string id, DateTime nowUtc)
    {
        Prune(nowUtc);
        return _pending.TryGetValue(id, out var p) ? p : null;
    }

    /// <summary>Counts a wrong proof; the code is withdrawn once it has seen too many.</summary>
    public void Fail(PendingPairing pending)
    {
        if (Interlocked.Increment(ref pending.Failures) >= MaxFailures) _pending.TryRemove(pending.Id, out _);
    }

    /// <summary>Takes the code out for good. False when another request already used it.</summary>
    public bool Consume(PendingPairing pending) => _pending.TryRemove(pending.Id, out _);

    /// <summary>Records what a redeemed code turned into.</summary>
    public void Complete(PendingPairing pending, string name, string credentialId, string peer, DateTime nowUtc)
        => _completed[pending.Id] = new CompletedPairing(pending.Id, name, credentialId, peer, nowUtc);

    public bool Cancel(string id) => _pending.TryRemove(id, out _);

    public IReadOnlyList<PendingPairing> Active(DateTime nowUtc)
    {
        Prune(nowUtc);
        return _pending.Values.OrderBy(p => p.ExpiresUtc).ToList();
    }

    /// <summary>Codes redeemed within <see cref="RecentFor"/>, newest first.</summary>
    public IReadOnlyList<CompletedPairing> Recent(DateTime nowUtc)
    {
        Prune(nowUtc);
        return _completed.Values.OrderByDescending(c => c.AtUtc).ToList();
    }

    private void Prune(DateTime nowUtc)
    {
        foreach (var (id, p) in _pending)
            if (p.ExpiresUtc <= nowUtc) _pending.TryRemove(id, out _);
        foreach (var (id, c) in _completed)
            if (nowUtc - c.AtUtc > RecentFor) _completed.TryRemove(id, out _);
    }
}
