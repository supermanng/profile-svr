using System.Security.Cryptography;
using System.Text;
using ProfileSvr.Domain;

namespace ProfileSvr.Common;

public static class Otp
{
    public const int Length = 6;
    public const int ExpiryMinutes = 10;
    public const int MaxAttempts = 5;
    public const int ResendCooldownSeconds = 60;
    public const int RetrievalCodeLength = 16;

    private const string RetrievalAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>Generates a cryptographically random numeric code, zero-padded to 6 digits.</summary>
    public static string GenerateCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    /// <summary>Generates a unique public reference for an OTP (unambiguous alphabet, no 0/O/1/I).</summary>
    public static string GenerateRetrievalCode() =>
        RandomNumberGenerator.GetString(RetrievalAlphabet, RetrievalCodeLength);

    /// <summary>
    /// SHA-256 hash binding the code to its purpose and owner (email, phone number, ...).
    /// A code can only verify for the exact purpose and target it was issued for.
    /// </summary>
    public static string Hash(string code, OtpPurpose purpose, string boundTo) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{code}|{purpose}|{boundTo.Trim().ToLowerInvariant()}")));

    public static bool Verify(string code, OtpPurpose purpose, string boundTo, string hash) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Hash(code, purpose, boundTo)),
            Encoding.UTF8.GetBytes(hash));
}
