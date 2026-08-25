using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Common.MessageCentre;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Features.Onboarding;

/// <summary>
/// First step of resuming onboarding for an SSO account with no record here:
/// sends an email OTP (purpose DeviceRegistration) so the device registered at
/// the resume step is approved by the mailbox owner, not just the password holder.
/// </summary>
public static class InitiateResume
{
    private record Response(string EmailAddress, string Channel, string RetrievalCode, DateTime ExpiresAtUtc);

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/onboarding/initiate-resume", Handle)
                .RequireAuthorization()
                .WithSummary("Send an email OTP approving device registration for an onboarding resume")
                .WithTags("Onboarding");
    }

    private static async Task<IResult> Handle(
        ClaimsPrincipal user,
        AppDbContext db,
        IMessageCentre messageCentre,
        CancellationToken ct)
    {
        var email = user.FindFirstValue("email")?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email))
            return ApiResults.Fail(StatusCodes.Status400BadRequest,
                "The token carries no email claim to resume with.");

        var existing = await ProfileClaims.ResolveProfileAsync(user, db, ct);
        if (existing is not null)
            return ApiResults.Conflict("A profile record already exists — nothing to resume.");

        var now = DateTime.UtcNow;
        var activeCodes = await db.OtpCodes
            .Where(o => o.Target == email &&
                        o.Purpose == OtpPurpose.DeviceRegistration &&
                        o.ConsumedAtUtc == null &&
                        o.ExpiresAtUtc > now)
            .ToListAsync(ct);

        if (activeCodes.Any(o => o.CreatedAtUtc > now.AddSeconds(-Otp.ResendCooldownSeconds)))
            return ApiResults.Fail(StatusCodes.Status429TooManyRequests,
                $"An OTP was sent recently. Wait {Otp.ResendCooldownSeconds} seconds before requesting another.");

        foreach (var stale in activeCodes)
            stale.ConsumedAtUtc = now;

        var code = Otp.GenerateCode();
        var otp = new OtpCode
        {
            Id = Guid.NewGuid(),
            RetrievalCode = Otp.GenerateRetrievalCode(),
            Purpose = OtpPurpose.DeviceRegistration,
            ProfileId = null,
            DeviceId = null,
            Channel = OtpChannel.Email,
            Target = email,
            CodeHash = Otp.Hash(code, OtpPurpose.DeviceRegistration, email),
            ExpiresAtUtc = now.AddMinutes(Otp.ExpiryMinutes),
            CreatedAtUtc = now
        };
        db.OtpCodes.Add(otp);
        await db.SaveChangesAsync(ct);

        try
        {
            await messageCentre.SendEmailOtpAsync(
                email, code,
                heading: "Approve this device",
                intro: "A request was made to register a new device and resume your account setup. Enter the code below to approve it. If this wasn't you, secure your account immediately.",
                ct);
        }
        catch (HttpRequestException ex)
        {
            otp.ConsumedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return ApiResults.Fail(StatusCodes.Status502BadGateway,
                $"Could not send the OTP email. {ex.Message}");
        }

        return ApiResults.Accepted(new Response(email, "email", otp.RetrievalCode, otp.ExpiresAtUtc),
            "OTP sent to your email address.");
    }
}
