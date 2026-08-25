using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Common.MessageCentre;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Features.Profiles;

public static class RequestPhoneOtp
{
    private record Response(Guid ProfileId, string Channel, string RetrievalCode, DateTime ExpiresAtUtc);

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/profiles/{id:guid}/request-phone-otp", Handle)
                .WithSummary("Generate an OTP and send it to the profile's phone via WhatsApp through the Message Centre")
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

        if (profile.PhoneNumberConfirmed)
            return ApiResults.Conflict("Phone number is already confirmed.");

        if (string.IsNullOrWhiteSpace(profile.PhoneNumber))
            return ApiResults.Fail(StatusCodes.Status422UnprocessableEntity, "The profile has no phone number. Set one first via PUT /api/profiles/{id}/phone.");

        var now = DateTime.UtcNow;

        var activeCodes = await db.OtpCodes
            .Where(o => o.ProfileId == id &&
                        o.Purpose == OtpPurpose.PhoneConfirmation &&
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
            Purpose = OtpPurpose.PhoneConfirmation,
            ProfileId = id,
            DeviceId = profile.DeviceId,
            Channel = messageCentre.PhoneOtpChannel,
            Target = profile.PhoneNumber,
            CodeHash = Otp.Hash(code, OtpPurpose.PhoneConfirmation, profile.PhoneNumber),
            ExpiresAtUtc = now.AddMinutes(Otp.ExpiryMinutes),
            CreatedAtUtc = now
        };
        db.OtpCodes.Add(otp);
        await db.SaveChangesAsync(ct);

        try
        {
            await messageCentre.SendPhoneOtpAsync(profile.PhoneNumber, code, ct);
        }
        catch (HttpRequestException ex)
        {
            otp.ConsumedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return ApiResults.Fail(StatusCodes.Status502BadGateway,
                $"Could not send the OTP via WhatsApp. {ex.Message}");
        }

        return ApiResults.Accepted(new Response(id, "whatsapp", otp.RetrievalCode, otp.ExpiresAtUtc));
    }
}
