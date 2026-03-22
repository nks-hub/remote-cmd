using System.Security.Cryptography;
using System.Text;

namespace RemoteCmd.Tests;

public class CryptoTests : IDisposable
{
    private const string ValidToken = "test-token-secure-24chars!";

    public CryptoTests()
    {
        Crypto.Reset();
    }

    public void Dispose()
    {
        Crypto.Reset();
    }

    [Fact]
    public void Encrypt_Decrypt_RoundTrip_ReturnsOriginalData()
    {
        Crypto.Init(ValidToken);
        var original = Encoding.UTF8.GetBytes("Hello, World!");

        var encrypted = Crypto.Encrypt(original);
        var decrypted = Crypto.Decrypt(encrypted);

        Assert.Equal(original, decrypted);
    }

    [Fact]
    public void EncryptString_DecryptString_RoundTrip_ReturnsOriginalText()
    {
        Crypto.Init(ValidToken);
        var original = "Hello, World! Ahoj, svete!";

        var encrypted = Crypto.EncryptString(original);
        var decrypted = Crypto.DecryptString(encrypted);

        Assert.Equal(original, decrypted);
    }

    [Fact]
    public void Decrypt_TamperedData_ThrowsCryptographicException()
    {
        Crypto.Init(ValidToken);
        var encrypted = Crypto.Encrypt(Encoding.UTF8.GetBytes("test data"));

        // Flip a byte in the ciphertext portion (after nonce+tag = 28 bytes)
        encrypted[30] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => Crypto.Decrypt(encrypted));
    }

    [Fact]
    public void Init_ShortToken_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Crypto.Init("short"));
    }

    [Fact]
    public void Init_TokenExactly23Chars_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Crypto.Init("12345678901234567890123"));
    }

    [Fact]
    public void Init_TokenExactly24Chars_Succeeds()
    {
        var ex = Record.Exception(() => Crypto.Init("123456789012345678901234"));
        Assert.Null(ex);
    }

    [Fact]
    public void Encrypt_BeforeInit_ThrowsInvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(() => Crypto.Encrypt(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void Decrypt_BeforeInit_ThrowsInvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(() => Crypto.Decrypt(new byte[30]));
    }

    [Fact]
    public void DifferentTokens_ProduceDifferentCiphertexts()
    {
        var data = Encoding.UTF8.GetBytes("same plaintext");

        Crypto.Init("token-aaaa-bbbb-cccc-dddd1");
        var enc1 = Crypto.Encrypt(data);

        Crypto.Reset();
        Crypto.Init("token-xxxx-yyyy-zzzz-wwww1");
        var enc2 = Crypto.Encrypt(data);

        // Ciphertexts must differ (different keys, plus random nonce)
        Assert.False(enc1.AsSpan().SequenceEqual(enc2));
    }

    [Fact]
    public void DifferentTokens_CannotCrossDecrypt()
    {
        var data = Encoding.UTF8.GetBytes("secret message");

        Crypto.Init("token-aaaa-bbbb-cccc-dddd1");
        var encrypted = Crypto.Encrypt(data);

        Crypto.Reset();
        Crypto.Init("token-xxxx-yyyy-zzzz-wwww1");

        Assert.ThrowsAny<CryptographicException>(() => Crypto.Decrypt(encrypted));
    }

    [Fact]
    public void NonceUniqueness_TwoEncryptsOfSameDataDiffer()
    {
        Crypto.Init(ValidToken);
        var data = Encoding.UTF8.GetBytes("same data");

        var enc1 = Crypto.Encrypt(data);
        var enc2 = Crypto.Encrypt(data);

        // Nonces (first 12 bytes) must differ
        Assert.False(enc1.AsSpan(0, 12).SequenceEqual(enc2.AsSpan(0, 12)));
        // Full ciphertexts must differ
        Assert.False(enc1.AsSpan().SequenceEqual(enc2));
    }

    [Fact]
    public void Encrypt_Decrypt_EmptyData_RoundTrips()
    {
        Crypto.Init(ValidToken);
        var empty = Array.Empty<byte>();

        var encrypted = Crypto.Encrypt(empty);
        var decrypted = Crypto.Decrypt(encrypted);

        Assert.Empty(decrypted);
    }

    [Fact]
    public void Encrypt_Decrypt_LargeData_1MB_RoundTrips()
    {
        Crypto.Init(ValidToken);
        var largeData = new byte[1024 * 1024]; // 1MB
        RandomNumberGenerator.Fill(largeData);

        var encrypted = Crypto.Encrypt(largeData);
        var decrypted = Crypto.Decrypt(encrypted);

        Assert.Equal(largeData, decrypted);
    }

    [Fact]
    public void Decrypt_TooShortData_ThrowsCryptographicException()
    {
        Crypto.Init(ValidToken);

        Assert.ThrowsAny<CryptographicException>(() => Crypto.Decrypt(new byte[10]));
    }

    [Fact]
    public void EncryptedData_HasCorrectFormat()
    {
        Crypto.Init(ValidToken);
        var data = Encoding.UTF8.GetBytes("test");

        var encrypted = Crypto.Encrypt(data);

        // nonce(12) + tag(16) + ciphertext(len(data))
        Assert.Equal(28 + data.Length, encrypted.Length);
    }
}
