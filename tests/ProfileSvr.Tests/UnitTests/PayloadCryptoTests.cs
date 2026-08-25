using System.Security.Cryptography;
using ProfileSvr.Common;

namespace ProfileSvr.Tests.UnitTests;

public class PayloadCryptoTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void EncryptDecrypt_RoundTrips()
    {
        var encrypted = PayloadCrypto.EncryptString(Key, """{"emailAddress":"a@b.com"}""");
        Assert.Equal("""{"emailAddress":"a@b.com"}""", PayloadCrypto.Decrypt(Key, encrypted));
    }

    [Fact]
    public void Encrypt_UsesFreshNonce_SamePlaintextDiffers()
    {
        var a = PayloadCrypto.EncryptString(Key, "payload");
        var b = PayloadCrypto.EncryptString(Key, "payload");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Decrypt_WrongKey_ReturnsNull()
    {
        var encrypted = PayloadCrypto.EncryptString(Key, "secret");
        Assert.Null(PayloadCrypto.Decrypt(RandomNumberGenerator.GetBytes(32), encrypted));
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_ReturnsNull()
    {
        var encrypted = PayloadCrypto.EncryptString(Key, "secret");
        var blob = Convert.FromBase64String(encrypted);
        blob[^1] ^= 0xFF;
        Assert.Null(PayloadCrypto.Decrypt(Key, Convert.ToBase64String(blob)));
    }

    [Theory]
    [InlineData("not-base64!!")]
    [InlineData("AAAA")] // too short
    public void Decrypt_Garbage_ReturnsNull(string input) =>
        Assert.Null(PayloadCrypto.Decrypt(Key, input));

    [Fact]
    public void ParseKey_RejectsMissingOrWrongSize()
    {
        Assert.Throws<InvalidOperationException>(() => PayloadCrypto.ParseKey(null));
        Assert.Throws<InvalidOperationException>(() =>
            PayloadCrypto.ParseKey(Convert.ToBase64String(new byte[16])));
    }
}
