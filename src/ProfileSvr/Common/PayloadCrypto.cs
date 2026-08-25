using System.Security.Cryptography;
using System.Text;

namespace ProfileSvr.Common;

/// <summary>
/// AES-256-GCM payload encryption. Wire format: base64(nonce[12] || ciphertext || tag[16]),
/// a fresh random nonce per message. The same key is shared with the client app
/// (Encryption:Key — base64-encoded 32 bytes).
/// </summary>
public static class PayloadCrypto
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static string Encrypt(byte[] key, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var blob = new byte[NonceSize + ciphertext.Length + TagSize];
        nonce.CopyTo(blob, 0);
        ciphertext.CopyTo(blob, NonceSize);
        tag.CopyTo(blob, NonceSize + ciphertext.Length);
        return Convert.ToBase64String(blob);
    }

    public static string EncryptString(byte[] key, string plaintext) =>
        Encrypt(key, Encoding.UTF8.GetBytes(plaintext));

    /// <summary>Returns null when the blob is malformed or fails authentication.</summary>
    public static string? Decrypt(byte[] key, string encoded)
    {
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return null;
        }

        if (blob.Length < NonceSize + TagSize)
            return null;

        var nonce = blob.AsSpan(0, NonceSize);
        var ciphertext = blob.AsSpan(NonceSize, blob.Length - NonceSize - TagSize);
        var tag = blob.AsSpan(blob.Length - TagSize, TagSize);
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }

        return Encoding.UTF8.GetString(plaintext);
    }

    public static byte[] ParseKey(string? base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
            throw new InvalidOperationException(
                "Encryption:Key is not configured. Generate one with: openssl rand -base64 32");
        var key = Convert.FromBase64String(base64Key);
        if (key.Length != 32)
            throw new InvalidOperationException("Encryption:Key must decode to exactly 32 bytes.");
        return key;
    }
}
