using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Common.MessageCentre;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Features.Profiles;

public static class RequestEmailOtp
{
    private record Response(Guid ProfileId, string Channel, string RetrievalCode, DateTime ExpiresAtUtc);

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/profiles/{id:guid}/request-email-otp", Handle)
                .WithSummary("Generate an OTP and send it to the profile's email via the Message Centre")
                .WithTags("Profiles");
    }

    private static async Task<IResult> Handle(
        Guid id,
        AppDbContext db,
        IMessageCentre messageCentre,
        CancellationToken ct)
    {
        var profile = await db.Profiles.FindAsync([id], ct);
        if (profile is null)
            return ApiResults.NotFound("Profile not found.");

        if (profile.EmailConfirmed)
            return ApiResults.Conflict("Email address is already confirmed.");

        var now = DateTime.UtcNow;

        var activeCodes = await db.OtpCodes
            .Where(o => o.ProfileId == id &&
                        o.Purpose == OtpPurpose.EmailConfirmation &&
                        o.ConsumedAtUtc == null &&
                        o.ExpiresAtUtc > now)
            .ToListAsync(ct);

        if (activeCodes.Any(o => o.CreatedAtUtc > now.AddSeconds(-Otp.ResendCooldownSeconds)))
            return ApiResults.Fail(StatusCodes.Status429TooManyRequests, $"An OTP was sent recently. Wait {Otp.ResendCooldownSeconds} seconds before requesting another.");

        // A new code invalidates any previous unconsumed ones.
        foreach (var stale in activeCodes)
            stale.ConsumedAtUtc = now;

        var code = Otp.GenerateCode();
        var otp = new OtpCode
        {
            Id = Guid.NewGuid(),
            RetrievalCode = Otp.GenerateRetrievalCode(),
            Purpose = OtpPurpose.EmailConfirmation,
            ProfileId = id,
            DeviceId = profile.DeviceId,
            Channel = OtpChannel.Email,
            Target = profile.EmailAddress,
            CodeHash = Otp.Hash(code, OtpPurpose.EmailConfirmation, profile.EmailAddress),
            ExpiresAtUtc = now.AddMinutes(Otp.ExpiryMinutes),
            CreatedAtUtc = now
        };
        db.OtpCodes.Add(otp);
        await db.SaveChangesAsync(ct);

        try
        {
            await messageCentre.SendEmailOtpAsync(
                profile.EmailAddress, code,
                heading: "Confirm your email address",
                intro: "Use the code below to continue. Enter it in the app to verify that this email address belongs to you.",
                ct);
        }
        catch (HttpRequestException ex)
        {
            otp.ConsumedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return ApiResults.Fail(StatusCodes.Status502BadGateway,
                $"Could not send the OTP email. {ex.Message}");
        }

        return ApiResults.Accepted(new Response(id, "email", otp.RetrievalCode, otp.ExpiresAtUtc));
    }
}
