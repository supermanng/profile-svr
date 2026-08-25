using Microsoft.EntityFrameworkCore;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Common;

/// <summary>
/// Shared OTP mechanics: issue an email code (cooldown + supersede) and check a submitted
/// code (active/purpose/profile/attempts/hash). Slices own persistence of the final state.
/// </summary>
public static class OtpFlow
{
    public record Issued(OtpCode Otp, string PlainCode);

    /// <summary>Issues an email OTP for the profile. Returns null when the cooldown blocks it (error set).</summary>
    public static async Task<(Issued? Issued, IResult? Error)> IssueEmailAsync(
        AppDbContext db, Profile profile, OtpPurpose purpose, Guid? deviceId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var activeCodes = await db.OtpCodes
            .Where(o => o.ProfileId == profile.Id &&
                        o.Purpose == purpose &&
                        o.ConsumedAtUtc == null &&
                        o.ExpiresAtUtc > now)
            .ToListAsync(ct);

        if (activeCodes.Any(o => o.CreatedAtUtc > now.AddSeconds(-Otp.ResendCooldownSeconds)))
            return (null, ApiResults.Fail(StatusCodes.Status429TooManyRequests,
                $"An OTP was sent recently. Wait {Otp.ResendCooldownSeconds} seconds before requesting another."));

        foreach (var stale in activeCodes)
            stale.ConsumedAtUtc = now;

        var code = Otp.GenerateCode();
        var otp = new OtpCode
        {
            Id = Guid.NewGuid(),
            RetrievalCode = Otp.GenerateRetrievalCode(),
            Purpose = purpose,
            ProfileId = profile.Id,
            DeviceId = deviceId,
            Channel = OtpChannel.Email,
            Target = profile.EmailAddress,
            CodeHash = Otp.Hash(code, purpose, profile.EmailAddress),
            ExpiresAtUtc = now.AddMinutes(Otp.ExpiryMinutes),
            CreatedAtUtc = now
        };
        db.OtpCodes.Add(otp);
        await db.SaveChangesAsync(ct);

        return (new Issued(otp, code), null);
    }

    /// <summary>
    /// Checks a submitted code against the stored OTP. On success returns the OTP with
    /// ConsumedAtUtc set (not yet saved) — the caller persists alongside its own changes.
    /// </summary>
    public static async Task<(OtpCode? Otp, IResult? Error)> CheckAsync(
        AppDbContext db, string retrievalCode, OtpPurpose purpose, Guid profileId,
        string boundTo, string submittedCode, string requestHint, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var otp = await db.OtpCodes.FirstOrDefaultAsync(o => o.RetrievalCode == retrievalCode, ct);

        if (otp is null || otp.ConsumedAtUtc is not null || otp.ExpiresAtUtc <= now)
            return (null, ApiResults.Fail(StatusCodes.Status400BadRequest,
                $"No active OTP matches this retrieval code. Request one via {requestHint}."));

        if (otp.Purpose != purpose)
            return (null, ApiResults.Fail(StatusCodes.Status400BadRequest,
                "This OTP was not issued for this operation."));

        if (otp.ProfileId != profileId)
            return (null, ApiResults.Fail(StatusCodes.Status400BadRequest,
                "This OTP was not issued for this profile."));

        if (otp.Attempts >= Otp.MaxAttempts)
        {
            otp.ConsumedAtUtc = now;
            await db.SaveChangesAsync(ct);
            return (null, ApiResults.Fail(StatusCodes.Status429TooManyRequests,
                "This code has been locked after too many failed attempts. Request a new one."));
        }

        if (!Otp.Verify(submittedCode, purpose, boundTo, otp.CodeHash))
        {
            otp.Attempts++;
            await db.SaveChangesAsync(ct);
            return (null, ApiResults.Fail(StatusCodes.Status400BadRequest, "The code is incorrect."));
        }

        otp.ConsumedAtUtc = now;
        return (otp, null);
    }
}
