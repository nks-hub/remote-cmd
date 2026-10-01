using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteCmd.Shared;

/// <summary>What a credential may do.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CredentialRole>))]
public enum CredentialRole
{
    /// <summary>A machine that runs commands: may only poll for work and answer it.</summary>
    Client,
    /// <summary>Whoever gives orders (MCP, curl, scripts): exec, transfers, the client list.</summary>
    Controller,
    /// <summary>A token from the command line or environment: both, as every token was before roles.</summary>
    Legacy,
}

/// <summary>
/// One accepted credential. The relay never keeps the token itself: a hash to recognise it and the
/// AES key it stands for are enough, so the config file cannot be replayed as a login.
/// </summary>
public sealed class Credential
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public CredentialRole Role { get; set; }
    public string TokenHash { get; set; } = "";
    public string Key { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastUsedUtc { get; set; }

    [JsonIgnore] public CryptoKey Crypto => _crypto ??= CryptoKey.FromKey(Convert.FromBase64String(Key));
    [JsonIgnore] private CryptoKey? _crypto;

    /// <summary>Not persisted: command-line tokens live only as long as the process that was given them.</summary>
    [JsonIgnore] public bool Ephemeral { get; set; }

    /// <summary>
    /// Masked form for dashboards: the label for stored credentials, a few characters of the token for
    /// command-line ones, which have no label.
    /// </summary>
    [JsonIgnore] public string Display { get; set; } = "";
}

/// <summary>On-disk settings of the relay.</summary>
public sealed class ServerSettings
{
    /// <summary>Address clients reach the relay at, used to print ready-made install commands.</summary>
    public string? PublicUrl { get; set; }
    /// <summary>PBKDF2 hash of the dashboard password, or null for an open (loopback-only) dashboard.</summary>
    public string? DashboardPassword { get; set; }
    public List<Credential> Credentials { get; set; } = [];
    /// <summary>MCP clients that registered for OAuth; kept so they need not register again after a restart.</summary>
    public List<OAuthClient> OAuthClients { get; set; } = [];
}

/// <summary>
/// Credentials and settings, kept in one JSON file. Every change is written straight back, so a
/// pairing survives a restart the moment it is made.
/// </summary>
public sealed class ServerStore
{
    private readonly string? _path;
    private readonly Lock _gate = new();
    private readonly ServerSettings _settings;
    private readonly ConcurrentDictionary<string, Credential> _byHash = new(StringComparer.Ordinal);

    private ServerStore(string? path, ServerSettings settings)
    {
        _path = path;
        _settings = settings;
        foreach (var c in settings.Credentials) _byHash[c.TokenHash] = c;
    }

    /// <summary>Opens the store at <paramref name="path"/>, creating an empty one if it is not there.</summary>
    public static ServerStore Open(string path)
    {
        ServerSettings settings;
        if (File.Exists(path))
        {
            settings = JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(path), Json) ?? new();
        }
        else
        {
            settings = new();
        }
        var store = new ServerStore(path, settings);
        if (!File.Exists(path)) store.Save();
        return store;
    }

    /// <summary>Default location: the machine-wide config directory, which a service can always read.</summary>
    public static string DefaultPath()
    {
        var fromEnv = Environment.GetEnvironmentVariable("REMOTECMD_CONFIG");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;
        return Path.Combine(ConfigPaths.MachineDirectory(), "server.json");
    }

    public string? PublicUrl
    {
        get => _settings.PublicUrl;
        set { lock (_gate) { _settings.PublicUrl = string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('/'); Save(); } }
    }

    public bool HasPassword => _settings.DashboardPassword != null;

    public void SetPassword(string? password)
    {
        lock (_gate)
        {
            _settings.DashboardPassword = string.IsNullOrEmpty(password) ? null : PasswordHash.Create(password);
            Save();
        }
    }

    public bool CheckPassword(string? password)
        => _settings.DashboardPassword is { } hash && password != null && PasswordHash.Verify(password, hash);

    /// <summary>Accepts a token given on the command line or in the environment, for this run only.</summary>
    public void AddEphemeral(string token)
    {
        var hash = HashToken(token);
        _byHash.TryAdd(hash, new Credential
        {
            Id = "cli-" + hash[..8],
            Label = "command-line token",
            Role = CredentialRole.Legacy,
            TokenHash = hash,
            Key = Convert.ToBase64String(CryptoKey.DeriveKey(token)),
            CreatedUtc = DateTime.UtcNow,
            Ephemeral = true,
            Display = token.Length <= 4 ? new string('*', token.Length) : $"{token[..2]}…{token[^2..]}",
        });
    }

    /// <summary>Issues a new credential. The token is returned once and kept nowhere.</summary>
    public (Credential Credential, string Token) Issue(string label, CredentialRole role)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var credential = new Credential
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Label = string.IsNullOrWhiteSpace(label) ? role.ToString().ToLowerInvariant() : label.Trim(),
            Role = role,
            TokenHash = HashToken(token),
            Key = Convert.ToBase64String(CryptoKey.DeriveKey(token)),
            CreatedUtc = DateTime.UtcNow,
        };
        credential.Display = credential.Label;
        lock (_gate)
        {
            _settings.Credentials.Add(credential);
            _byHash[credential.TokenHash] = credential;
            Save();
        }
        return (credential, token);
    }

    /// <summary>
    /// Stores a token that already exists — one clients were set up with by hand — so the relay can
    /// stop taking it on its command line. Only its hash and key are kept.
    /// </summary>
    public Credential Import(string token, string label, CredentialRole role)
    {
        var hash = HashToken(token);
        lock (_gate)
        {
            if (_settings.Credentials.FirstOrDefault(c => c.TokenHash == hash) is { } existing) return existing;
            var credential = new Credential
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Label = string.IsNullOrWhiteSpace(label) ? "imported" : label.Trim(),
                Role = role,
                TokenHash = hash,
                Key = Convert.ToBase64String(CryptoKey.DeriveKey(token)),
                CreatedUtc = DateTime.UtcNow,
            };
            credential.Display = credential.Label;
            _settings.Credentials.Add(credential);
            _byHash[hash] = credential;
            Save();
            return credential;
        }
    }

    public OAuthClient RegisterOAuthClient(OAuthClient client)
    {
        lock (_gate)
        {
            // Every MCP client registers itself, possibly on each run; the list must not grow forever.
            if (_settings.OAuthClients.Count >= 200) _settings.OAuthClients.RemoveAt(0);
            _settings.OAuthClients.Add(client);
            Save();
            return client;
        }
    }

    public OAuthClient? FindOAuthClient(string clientId)
    {
        lock (_gate) return _settings.OAuthClients.FirstOrDefault(c => c.ClientId == clientId);
    }

    public bool Revoke(string id)
    {
        lock (_gate)
        {
            var credential = _settings.Credentials.FirstOrDefault(c => c.Id == id);
            if (credential == null) return false;
            _settings.Credentials.Remove(credential);
            _byHash.TryRemove(credential.TokenHash, out _);
            Save();
            return true;
        }
    }

    public Credential? Authenticate(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (!_byHash.TryGetValue(HashToken(token), out var credential)) return null;
        // Kept in memory and written with the next change: a disk write per poll would be absurd.
        credential.LastUsedUtc = DateTime.UtcNow;
        if (credential.Display.Length == 0) credential.Display = credential.Label;
        return credential;
    }

    /// <summary>Everything that is accepted right now, command-line tokens included.</summary>
    public IReadOnlyList<Credential> All() => _byHash.Values.OrderBy(c => c.CreatedUtc).ToList();

    public int Count => _byHash.Count;

    /// <summary>Callers that change settings hold <see cref="_gate"/> already.</summary>
    private void Save()
    {
        if (_path == null) return;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Written beside and moved over, so a crash mid-write cannot leave a truncated file
        // that loses every pairing at once.
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_settings, Json));
        ConfigPaths.RestrictToOwner(tmp);
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>
    /// Tokens are 256 random bits, so a plain hash is enough to recognise one: there is nothing to
    /// brute-force that a slow hash would protect. The prefix keeps it distinct from the AES key.
    /// </summary>
    public static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("RemoteCmd:auth:v1:" + token)));

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
}

/// <summary>PBKDF2-SHA256 password hashes in the form <c>iterations.salt.hash</c>.</summary>
public static class PasswordHash
{
    private const int Iterations = 210_000;

    public static string Create(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
