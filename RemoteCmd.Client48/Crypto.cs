using System;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace RemoteCmd.Client48
{
    /// <summary>
    /// AES-256-GCM wire-compatible with the .NET 9 server/client. Key is
    /// SHA256("RemoteCmd:v1:"+token); wire format is nonce(12)+tag(16)+ciphertext(N).
    /// .NET Framework 4.8 lacks AesGcm, so BouncyCastle provides the GCM mode.
    /// </summary>
    internal static class Crypto
    {
        private const int NonceLen = 12;
        private const int TagLen = 16;
        private static byte[] _key;
        private static readonly RNGCryptoServiceProvider Rng = new RNGCryptoServiceProvider();

        public static void Init(string token)
        {
            using (var sha = SHA256.Create())
                _key = sha.ComputeHash(Encoding.UTF8.GetBytes("RemoteCmd:v1:" + token));
        }

        public static byte[] Encrypt(byte[] data)
        {
            var nonce = new byte[NonceLen];
            Rng.GetBytes(nonce);

            var cipher = new GcmBlockCipher(new AesEngine());
            cipher.Init(true, new AeadParameters(new KeyParameter(_key), TagLen * 8, nonce));

            // BouncyCastle emits ciphertext followed by the tag.
            var ctAndTag = new byte[cipher.GetOutputSize(data.Length)];
            int len = cipher.ProcessBytes(data, 0, data.Length, ctAndTag, 0);
            cipher.DoFinal(ctAndTag, len);

            int ctLen = data.Length;
            var result = new byte[NonceLen + TagLen + ctLen];
            Buffer.BlockCopy(nonce, 0, result, 0, NonceLen);
            Buffer.BlockCopy(ctAndTag, ctLen, result, NonceLen, TagLen);          // tag
            Buffer.BlockCopy(ctAndTag, 0, result, NonceLen + TagLen, ctLen);      // ciphertext
            return result;
        }

        public static byte[] Decrypt(byte[] data)
        {
            if (data.Length < NonceLen + TagLen)
                throw new CryptographicException("Invalid encrypted data");

            var nonce = new byte[NonceLen];
            var tag = new byte[TagLen];
            int ctLen = data.Length - NonceLen - TagLen;
            var ctAndTag = new byte[ctLen + TagLen];

            Buffer.BlockCopy(data, 0, nonce, 0, NonceLen);
            Buffer.BlockCopy(data, NonceLen, tag, 0, TagLen);
            Buffer.BlockCopy(data, NonceLen + TagLen, ctAndTag, 0, ctLen);        // ciphertext
            Buffer.BlockCopy(tag, 0, ctAndTag, ctLen, TagLen);                    // tag appended

            var cipher = new GcmBlockCipher(new AesEngine());
            cipher.Init(false, new AeadParameters(new KeyParameter(_key), TagLen * 8, nonce));

            var plain = new byte[cipher.GetOutputSize(ctAndTag.Length)];
            int len = cipher.ProcessBytes(ctAndTag, 0, ctAndTag.Length, plain, 0);
            len += cipher.DoFinal(plain, len);
            if (len != plain.Length)
            {
                var trimmed = new byte[len];
                Buffer.BlockCopy(plain, 0, trimmed, 0, len);
                return trimmed;
            }
            return plain;
        }

        public static string DecryptString(string base64)
            => Encoding.UTF8.GetString(Decrypt(Convert.FromBase64String(base64)));
    }
}
