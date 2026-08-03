using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace RemoteCmd.Shared;

/// <summary>
/// AES-256-GCM payload encryption. The key is derived from the shared token via SHA256, so every
/// token has its own key — a relay serving several tokens must encrypt each client's traffic with
/// the token that client authenticated with (see <see cref="Crypto.For"/>).
/// Wire format: nonce(12) + tag(16) + ciphertext(N).
/// </summary>
public sealed class CryptoKey
{
    private readonly byte[] _key;

    public CryptoKey(string token)
        => _key = SHA256.HashData(Encoding.UTF8.GetBytes("RemoteCmd:v1:" + token));

    public byte[] Encrypt(byte[] data)
    {
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[data.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, data, ciphertext, tag);

        var result = new byte[28 + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, 12);
        Buffer.BlockCopy(tag, 0, result, 12, 16);
        Buffer.BlockCopy(ciphertext, 0, result, 28, ciphertext.Length);
        return result;
    }

    public byte[] Decrypt(byte[] data)
    {
        if (data.Length < 28) throw new CryptographicException("Invalid encrypted data");

        var nonce = new byte[12];
        var tag = new byte[16];
        var ciphertext = new byte[data.Length - 28];

        Buffer.BlockCopy(data, 0, nonce, 0, 12);
        Buffer.BlockCopy(data, 12, tag, 0, 16);
        Buffer.BlockCopy(data, 28, ciphertext, 0, ciphertext.Length);

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    public string EncryptString(string text)
        => Convert.ToBase64String(Encrypt(Encoding.UTF8.GetBytes(text)));

    public string DecryptString(string base64)
        => Encoding.UTF8.GetString(Decrypt(Convert.FromBase64String(base64)));
}

/// <summary>
/// Key cache plus the single-token helpers used by the client, which only ever talks to one relay
/// with one token.
/// </summary>
public static class Crypto
{
    private static readonly ConcurrentDictionary<string, CryptoKey> Keys = new(StringComparer.Ordinal);
    private static CryptoKey? _default;

    /// <summary>Key for a specific token; cached so lookups on the polling path stay cheap.</summary>
    public static CryptoKey For(string token) => Keys.GetOrAdd(token, t => new CryptoKey(t));

    /// <summary>Set the key used by the parameterless helpers below.</summary>
    public static void Init(string token) => _default = For(token);

    /// <summary>Reset key state. Test-only.</summary>
    internal static void Reset() => _default = null;

    private static CryptoKey Default => _default ?? throw new InvalidOperationException("Crypto.Init was not called");

    public static byte[] Encrypt(byte[] data) => Default.Encrypt(data);

    public static byte[] Decrypt(byte[] data) => Default.Decrypt(data);

    public static string EncryptString(string text) => Default.EncryptString(text);

    public static string DecryptString(string base64) => Default.DecryptString(base64);
}
