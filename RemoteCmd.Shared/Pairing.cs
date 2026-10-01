using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteCmd.Shared;

/// <summary>What the client sends to <c>POST /api/pair</c>.</summary>
public sealed class PairRequest
{
    /// <summary>Which pending code this is for (<see cref="Pairing.CodeId"/>), so wrong guesses count against it.</summary>
    [JsonPropertyName("codeId")] public string CodeId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>The client's ephemeral P-256 public key (SubjectPublicKeyInfo, base64).</summary>
    [JsonPropertyName("publicKey")] public string PublicKey { get; set; } = "";
    /// <summary>HMAC of the public key under the pairing code: proves the client knows the code.</summary>
    [JsonPropertyName("proof")] public string Proof { get; set; } = "";
}

/// <summary>The relay's answer to a pairing request.</summary>
public sealed class PairResponse
{
    [JsonPropertyName("publicKey")] public string PublicKey { get; set; } = "";
    /// <summary>HMAC of both public keys under the code: proves the relay knows it too.</summary>
    [JsonPropertyName("proof")] public string Proof { get; set; } = "";
    /// <summary><see cref="PairGrant"/>, encrypted with the key both sides derived.</summary>
    [JsonPropertyName("grant")] public string Grant { get; set; } = "";
}

/// <summary>What a successful pairing hands the client.</summary>
public sealed class PairGrant
{
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("credentialId")] public string CredentialId { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>SHA-256 of the relay's TLS certificate, for the client to pin; null without TLS.</summary>
    [JsonPropertyName("certSha256")] public string? CertSha256 { get; set; }
}

/// <summary>
/// Pairing turns a short one-time code into a long per-device token without that token ever
/// crossing the network in a form someone listening could use.
///
/// Both sides run an ephemeral ECDH exchange and each proves it knows the code with an HMAC over
/// the public keys. Someone who only listens sees two public keys and two MACs: the shared secret
/// needs a private key, so the token stays out of reach even over plain HTTP. Someone who sits in
/// the middle must answer with the code before it expires, and the relay burns a code after a few
/// wrong proofs. The token is then encrypted under a key derived from both the ECDH secret and the
/// code.
/// </summary>
public static class Pairing
{
    // Crockford base32 without the letters that read as digits (I, L, O, U): codes get typed by hand.
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int CodeLength = 10;

    /// <summary>A fresh code, shown as two groups of five: <c>K7F2Q-9XDMA</c>. 50 bits.</summary>
    public static string NewCode()
    {
        Span<char> chars = stackalloc char[CodeLength];
        for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return $"{new string(chars[..5])}-{new string(chars[5..])}";
    }

    /// <summary>Upper-case, dashes and spaces dropped, and the look-alikes mapped the Crockford way.</summary>
    public static string Normalize(string code)
    {
        var sb = new StringBuilder(code.Length);
        foreach (var c in code.ToUpperInvariant())
        {
            if (c is '-' or ' ') continue;
            sb.Append(c switch { 'O' => '0', 'I' or 'L' => '1', _ => c });
        }
        return sb.ToString();
    }

    /// <summary>Starts a pairing on the client side. Keep the returned state for <see cref="Finish"/>.</summary>
    public static (PairRequest Request, ECDiffieHellman State) Begin(string code, string name)
    {
        var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var pub = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        return (new PairRequest
        {
            CodeId = CodeId(code),
            Name = name,
            PublicKey = Convert.ToBase64String(pub),
            Proof = Convert.ToBase64String(Mac(code, "client", pub)),
        }, ecdh);
    }

    /// <summary>
    /// The relay's half. Returns null when the proof does not match the code, which the caller
    /// must count as a failed attempt against that code. <paramref name="issue"/> runs only once the
    /// proof checks out, so nothing is granted to a wrong guess; returning null from it aborts.
    /// </summary>
    public static PairResponse? Answer(string code, PairRequest request, Func<PairGrant?> issue)
    {
        byte[] clientPub;
        try { clientPub = Convert.FromBase64String(request.PublicKey); }
        catch (FormatException) { return null; }

        byte[] proof;
        try { proof = Convert.FromBase64String(request.Proof); }
        catch (FormatException) { return null; }
        if (!CryptographicOperations.FixedTimeEquals(proof, Mac(code, "client", clientPub))) return null;

        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var peer = ECDiffieHellman.Create();
        try { peer.ImportSubjectPublicKeyInfo(clientPub, out _); }
        catch (CryptographicException) { return null; }

        var grant = issue();
        if (grant is null) return null;

        var serverPub = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        var key = SessionKey(code, ecdh, peer.PublicKey);
        return new PairResponse
        {
            PublicKey = Convert.ToBase64String(serverPub),
            Proof = Convert.ToBase64String(Mac(code, "server", [.. serverPub, .. clientPub])),
            Grant = Convert.ToBase64String(CryptoKey.FromKey(key).Encrypt(JsonSerializer.SerializeToUtf8Bytes(grant))),
        };
    }

    /// <summary>
    /// The client's second half: checks that the relay knew the code and opens the grant.
    /// Throws <see cref="CryptographicException"/> when either check fails.
    /// </summary>
    public static PairGrant Finish(string code, ECDiffieHellman state, PairResponse response)
    {
        var serverPub = Convert.FromBase64String(response.PublicKey);
        var clientPub = state.PublicKey.ExportSubjectPublicKeyInfo();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(response.Proof), Mac(code, "server", [.. serverPub, .. clientPub])))
            throw new CryptographicException("The relay did not prove it knows the pairing code");

        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(serverPub, out _);
        var key = SessionKey(code, state, peer.PublicKey);
        var json = CryptoKey.FromKey(key).Decrypt(Convert.FromBase64String(response.Grant));
        return JsonSerializer.Deserialize<PairGrant>(json)
               ?? throw new CryptographicException("Empty pairing grant");
    }

    /// <summary>
    /// A short public handle for a code. It lets someone who captured it test guesses offline — but the
    /// proof in the same request allows exactly that already, and 50 bits do not fall within a
    /// code's ten-minute life.
    /// </summary>
    public static string CodeId(string code)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("RemoteCmd:pair-id:v1:" + Normalize(code))))[..12];

    private static byte[] CodeKey(string code)
        => SHA256.HashData(Encoding.UTF8.GetBytes("RemoteCmd:pair:v1:" + Normalize(code)));

    private static byte[] Mac(string code, string role, byte[] data)
    {
        byte[] message = [.. Encoding.UTF8.GetBytes(role), .. data];
        return HMACSHA256.HashData(CodeKey(code), message);
    }

    private static byte[] SessionKey(string code, ECDiffieHellman own, ECDiffieHellmanPublicKey other)
        => own.DeriveKeyFromHmac(other, HashAlgorithmName.SHA256, CodeKey(code));
}
