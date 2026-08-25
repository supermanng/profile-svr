using System.Security.Cryptography;

namespace ProfileSvr.Common;

/// <summary>
/// Transaction PIN hashing: PBKDF2-SHA256 with a per-profile random salt.
/// </summary>
public static class TransactionPin
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 100_000;

    public static string GenerateSalt() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltSize));

    public static string Hash(string pin, string salt) =>
        Convert.ToHexString(Rfc2898DeriveBytes.Pbkdf2(
            pin, Convert.FromBase64String(salt), Iterations, HashAlgorithmName.SHA256, HashSize));

    public static bool Verify(string pin, string salt, string hash) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(Hash(pin, salt)),
            Convert.FromHexString(hash));
}
