using System.Security.Cryptography;
using System.Text;

public static class Crypto
{
    private const int MinTokenLength = 24;
    private const string HkdfInfo = "RemoteCmd:v2:aes-gcm-key";

    private static byte[]? _key;

    /// <summary>
    /// Derives AES-256 key from shared token using HKDF (RFC 5869).
    /// Token must be at least 24 characters.
    /// </summary>
    public static void Init(string token)
    {
        if (token.Length < MinTokenLength)
            throw new ArgumentException($"Token must be at least {MinTokenLength} characters.", nameof(token));

        var ikm = Encoding.UTF8.GetBytes(token);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm);
        _key = HKDF.Expand(HashAlgorithmName.SHA256, prk, 32, Encoding.UTF8.GetBytes(HkdfInfo));
    }

    public static byte[] Encrypt(byte[] data)
    {
        if (_key is null) throw new InvalidOperationException("Crypto.Init() must be called before Encrypt.");

        Span<byte> nonce = stackalloc byte[12];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[data.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, data, ciphertext, tag);

        // Format: nonce(12) + tag(16) + ciphertext(N)
        var result = new byte[28 + ciphertext.Length];
        nonce.CopyTo(result.AsSpan(0, 12));
        tag.AsSpan().CopyTo(result.AsSpan(12, 16));
        ciphertext.AsSpan().CopyTo(result.AsSpan(28));
        return result;
    }

    public static byte[] Decrypt(byte[] data)
    {
        if (_key is null) throw new InvalidOperationException("Crypto.Init() must be called before Decrypt.");
        if (data.Length < 28) throw new CryptographicException("Invalid encrypted data");

        var span = data.AsSpan();
        var nonce = span[..12];
        var tag = span[12..28];
        var ciphertext = span[28..];

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    public static string EncryptString(string text)
        => Convert.ToBase64String(Encrypt(Encoding.UTF8.GetBytes(text)));

    public static string DecryptString(string base64)
        => Encoding.UTF8.GetString(Decrypt(Convert.FromBase64String(base64)));

    /// <summary>
    /// Resets the derived key. Intended for use in unit tests only.
    /// </summary>
    internal static void Reset() => _key = null;
}
