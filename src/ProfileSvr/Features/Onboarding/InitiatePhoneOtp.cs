using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Common.MessageCentre;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Features.Onboarding;

/// <summary>
/// Phone leg of onboarding: after the profile is created (email verified), the client
/// submits the phone number here — it is stored on the profile (unconfirmed) and an OTP
/// is sent over WhatsApp. Verification happens via POST /api/otp/verify (section Phone).
/// </summary>
public static class InitiatePhoneOtp
{
    public record Request(string PhoneNumber, Guid DeviceId);

    private record Response(Guid ProfileId, string PhoneNumber, string Channel, string RetrievalCode, DateTime ExpiresAtUtc);

    public class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(r => r.DeviceId).NotEmpty();
            RuleFor(r => r.PhoneNumber)
                .NotEmpty()
                .MaximumLength(32)
                .Matches(@"^\+?[0-9\s\-()]{7,32}$")
                .WithMessage("Phone number must contain 7-32 digits and may include +, spaces, dashes or parentheses.");
        }
    }

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/onboarding/initiate-phone", Handle)
                .RequireAuthorization(TokenTypes.OnboardingPolicy)
                .WithSummary("Set the onboarding profile's phone number and send a WhatsApp OTP to confirm it (requires login)")
                .WithTags("Onboarding");
    }

    private static async Task<IResult> Handle(
        Request request,
        AppDbContext db,
        IMessageCentre messageCentre,
        IValidator<Request> validator,
        CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ApiResults.ValidationFailed(validation);

        // The device's active binding identifies the onboarding profile.
        var profileId = await db.UserDevices
            .Where(ud => ud.DeviceId == request.DeviceId && ud.ReleasedAtUtc == null)
            .Select(ud => (Guid?)ud.ProfileId)
            .FirstOrDefaultAsync(ct);
        if (profileId is null)
            return ApiResults.Fail(StatusCodes.Status422UnprocessableEntity,
                "This device is not linked to any profile. Complete onboarding verification first.");

        var profile = await db.Profiles.FindAsync([profileId.Value], ct);
        if (profile is null)
            return ApiResults.NotFound("Profile not found.");

        if (profile.PhoneNumberConfirmed)
            return ApiResults.Conflict("Phone number is already confirmed.");

        var phone = request.PhoneNumber.Trim();

        var taken = await db.Profiles.AnyAsync(p => p.Id != profile.Id && p.PhoneNumber == phone, ct);
        if (taken)
            return ApiResults.Conflict("Another profile already uses this phone number.");

        var now = DateTime.UtcNow;

        var activeCodes = await db.OtpCodes
            .Where(o => o.ProfileId == profile.Id &&
                        o.Purpose == OtpPurpose.PhoneConfirmation &&
                        o.ConsumedAtUtc == null &&
                        o.ExpiresAtUtc > now)
            .ToListAsync(ct);

        if (activeCodes.Any(o => o.CreatedAtUtc > now.AddSeconds(-Otp.ResendCooldownSeconds)))
            return ApiResults.Fail(StatusCodes.Status429TooManyRequests,
                $"An OTP was sent recently. Wait {Otp.ResendCooldownSeconds} seconds before requesting another.");

        foreach (var stale in activeCodes)
            stale.ConsumedAtUtc = now;

        profile.PhoneNumber = phone;
        profile.PhoneNumberConfirmed = false;
        profile.UpdatedAtUtc = now;
        ActivityLog.Record(db, ActivityType.PhoneNumberSet, profile.Id, request.DeviceId, $"Phone number {phone} set.");

        var code = Otp.GenerateCode();
        var otp = new OtpCode
        {
            Id = Guid.NewGuid(),
            RetrievalCode = Otp.GenerateRetrievalCode(),
            Purpose = OtpPurpose.PhoneConfirmation,
            ProfileId = profile.Id,
            DeviceId = request.DeviceId,
            Channel = messageCentre.PhoneOtpChannel,
            Target = phone,
            CodeHash = Otp.Hash(code, OtpPurpose.PhoneConfirmation, phone),
            ExpiresAtUtc = now.AddMinutes(Otp.ExpiryMinutes),
            CreatedAtUtc = now
        };
        db.OtpCodes.Add(otp);
        await db.SaveChangesAsync(ct);

        try
        {
            await messageCentre.SendPhoneOtpAsync(phone, code, ct);
        }
        catch (HttpRequestException ex)
        {
            otp.ConsumedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return ApiResults.Fail(StatusCodes.Status502BadGateway,
                $"Could not send the OTP via WhatsApp. {ex.Message}");
        }

        return ApiResults.Accepted(new Response(profile.Id, phone, "whatsapp", otp.RetrievalCode, otp.ExpiresAtUtc),
            "OTP sent to your phone number.");
    }
}
