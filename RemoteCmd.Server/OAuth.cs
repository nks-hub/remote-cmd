using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

/// <summary>An MCP client that registered itself (RFC 7591 dynamic client registration).</summary>
public sealed class OAuthClient
{
    public string ClientId { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> RedirectUris { get; set; } = [];
    public DateTime CreatedUtc { get; set; }
}

/// <summary>An authorization request waiting for someone to approve it on the dashboard.</summary>
public sealed class PendingAuthorization
{
    public string Id { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string ClientName { get; init; } = "";
    public string RedirectUri { get; init; } = "";
    public string CodeChallenge { get; init; } = "";
    public string? State { get; init; }
    /// <summary>Shown both on the agent's consent page and on the dashboard, so the two can be matched up.</summary>
    public string UserCode { get; init; } = "";
    public DateTime CreatedUtc { get; init; }
    public DateTime ExpiresUtc { get; init; }

    /// <summary>pending, approved or denied.</summary>
    public string Status { get; set; } = "pending";
    /// <summary>Authorization code handed out once approved; null until then.</summary>
    public string? Code { get; set; }

    [JsonIgnore]
    public string RedirectHost => Uri.TryCreate(RedirectUri, UriKind.Absolute, out var u) ? u.Authority : RedirectUri;
}

/// <summary>
/// The OAuth 2.1 authorization server MCP clients sign in through. Nothing is granted on the
/// strength of a browser visit alone: every authorization waits until someone approves it on the
/// relay's dashboard, which only this machine can open. Approval issues a controller credential
/// named after the agent, so it shows up — and can be revoked — next to every other credential.
///
/// Public clients only (no client secret), so PKCE with S256 is mandatory.
/// </summary>
public sealed class OAuthServer(ServerStore store)
{
    public static readonly TimeSpan RequestLife = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan CodeLife = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, PendingAuthorization> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingAuthorization> _codes = new(StringComparer.Ordinal);

    /// <summary>
    /// Loopback redirects (where native apps such as Claude Code listen) and https only: an http
    /// redirect to another host would hand the code to anyone on the path.
    /// </summary>
    public static bool IsAcceptableRedirect(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || !string.IsNullOrEmpty(u.Fragment)) return false;
        if (u.Scheme == Uri.UriSchemeHttps) return true;
        return u.Scheme == Uri.UriSchemeHttp && (u.IsLoopback || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
    }

    public OAuthClient Register(string? name, IReadOnlyList<string> redirectUris)
        => store.RegisterOAuthClient(new OAuthClient
        {
            ClientId = "mcp-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant(),
            Name = string.IsNullOrWhiteSpace(name) ? "MCP client" : new string(name.Where(c => !char.IsControl(c)).Take(80).ToArray()),
            RedirectUris = [.. redirectUris],
            CreatedUtc = DateTime.UtcNow,
        });

    public OAuthClient? FindClient(string? clientId) => clientId is null ? null : store.FindOAuthClient(clientId);

    public PendingAuthorization Request(OAuthClient client, string redirectUri, string codeChallenge, string? state, DateTime nowUtc)
    {
        Prune(nowUtc);
        var request = new PendingAuthorization
        {
            Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            ClientId = client.ClientId,
            ClientName = client.Name,
            RedirectUri = redirectUri,
            CodeChallenge = codeChallenge,
            State = state,
            UserCode = $"{RandomNumberGenerator.GetInt32(1000):000}-{RandomNumberGenerator.GetInt32(1000):000}",
            CreatedUtc = nowUtc,
            ExpiresUtc = nowUtc + RequestLife,
        };
        _requests[request.Id] = request;
        return request;
    }

    public PendingAuthorization? Find(string id, DateTime nowUtc)
    {
        Prune(nowUtc);
        return _requests.TryGetValue(id, out var r) ? r : null;
    }

    public IReadOnlyList<PendingAuthorization> Pending(DateTime nowUtc)
    {
        Prune(nowUtc);
        return _requests.Values.Where(r => r.Status == "pending").OrderBy(r => r.CreatedUtc).ToList();
    }

    public bool Decide(string id, bool approve, DateTime nowUtc)
    {
        var request = Find(id, nowUtc);
        if (request is null || request.Status != "pending") return false;
        lock (request)
        {
            if (request.Status != "pending") return false;
            if (approve)
            {
                request.Code = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                _codes[request.Code] = request;
                request.Status = "approved";
            }
            else
            {
                request.Status = "denied";
            }
        }
        return true;
    }

    /// <summary>
    /// Trades an approved code for a credential. Single use: the code is gone whether or not the
    /// exchange succeeds, so a stolen one cannot be retried.
    /// </summary>
    public (string? Token, string? Error) Exchange(string? code, string? clientId, string? redirectUri, string? verifier, DateTime nowUtc)
    {
        if (code is null || !_codes.TryRemove(code, out var request)) return (null, "invalid_grant");
        _requests.TryRemove(request.Id, out _);
        if (request.CreatedUtc + RequestLife + CodeLife < nowUtc) return (null, "invalid_grant");
        if (request.ClientId != clientId || request.RedirectUri != redirectUri) return (null, "invalid_grant");
        if (string.IsNullOrEmpty(verifier) || !VerifierMatches(verifier, request.CodeChallenge)) return (null, "invalid_grant");

        var (_, token) = store.Issue($"MCP: {request.ClientName}", CredentialRole.Controller);
        return (token, null);
    }

    public static bool VerifierMatches(string verifier, string challenge)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var computed = Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(challenge));
    }

    private void Prune(DateTime nowUtc)
    {
        foreach (var (id, r) in _requests)
            if (r.ExpiresUtc <= nowUtc && r.Status == "pending") _requests.TryRemove(id, out _);
        foreach (var (code, r) in _codes)
            if (r.CreatedUtc + RequestLife + CodeLife < nowUtc) _codes.TryRemove(code, out _);
    }
}
