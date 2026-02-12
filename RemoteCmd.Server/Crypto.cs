using System.Security.Cryptography;
using System.Text;

static class Crypto
{
    private static byte[] _key = null!;

    /// <summary>
    /// Derive AES-256 key from shared token using SHA256
    /// </summary>
    public static void Init(string token)
    {
        _key = SHA256.HashData(Encoding.UTF8.GetBytes("RemoteCmd:v1:" + token));
    }

    public static byte[] Encrypt(byte[] data)
    {
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[data.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, data, ciphertext, tag);

        // Format: nonce(12) + tag(16) + ciphertext(N)
        var result = new byte[28 + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, 12);
        Buffer.BlockCopy(tag, 0, result, 12, 16);
        Buffer.BlockCopy(ciphertext, 0, result, 28, ciphertext.Length);
        return result;
    }

    public static byte[] Decrypt(byte[] data)
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

    public static string EncryptString(string text)
        => Convert.ToBase64String(Encrypt(Encoding.UTF8.GetBytes(text)));

    public static string DecryptString(string base64)
        => Encoding.UTF8.GetString(Decrypt(Convert.FromBase64String(base64)));
}
