using System.Security.Cryptography;
using System.Text;
using RemoteCmd.Shared;
using Xunit;

namespace RemoteCmd.Tests;

[Collection("CryptoSerial")]
public class CryptoTests
{
    public CryptoTests() => Crypto.Init("test-token-1234567890ab");

    [Fact]
    public void Roundtrip_String_Succeeds()
    {
        var plaintext = "Hello, multi-client world!";
        var encrypted = Crypto.EncryptString(plaintext);
        Assert.NotEqual(plaintext, encrypted);
        Assert.Equal(plaintext, Crypto.DecryptString(encrypted));
    }

    [Fact]
    public void Roundtrip_Bytes_Succeeds()
    {
        var data = Enumerable.Range(0, 1024).Select(i => (byte)(i % 256)).ToArray();
        var encrypted = Crypto.Encrypt(data);
        var decrypted = Crypto.Decrypt(encrypted);
        Assert.Equal(data, decrypted);
    }

    [Fact]
    public void Encrypt_ProducesNonceTagCiphertext_WithOverhead28()
    {
        var data = new byte[100];
        var encrypted = Crypto.Encrypt(data);
        Assert.Equal(100 + 28, encrypted.Length);
    }

    [Fact]
    public void Encrypt_TwiceWithSameInput_ProducesDifferentOutput_BecauseNonceRandom()
    {
        var data = Encoding.UTF8.GetBytes("same input");
        var a = Crypto.Encrypt(data);
        var b = Crypto.Encrypt(data);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Decrypt_TamperedTag_Throws()
    {
        var encrypted = Crypto.Encrypt(Encoding.UTF8.GetBytes("payload"));
        encrypted[20] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => Crypto.Decrypt(encrypted));
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_Throws()
    {
        var encrypted = Crypto.Encrypt(Encoding.UTF8.GetBytes("payload"));
        encrypted[^1] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => Crypto.Decrypt(encrypted));
    }

    [Fact]
    public void Decrypt_TooShort_Throws()
    {
        Assert.Throws<CryptographicException>(() => Crypto.Decrypt(new byte[10]));
    }

    [Fact]
    public void Init_DifferentTokens_ProduceDifferentKeys()
    {
        Crypto.Init("token-one-11111111111111");
        var a = Crypto.Encrypt(Encoding.UTF8.GetBytes("x"));
        Crypto.Init("token-two-22222222222222");
        Assert.ThrowsAny<CryptographicException>(() => Crypto.Decrypt(a));
    }
}
