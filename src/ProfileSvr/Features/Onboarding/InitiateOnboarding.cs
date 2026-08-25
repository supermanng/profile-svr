using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Common.MessageCentre;
using ProfileSvr.Common.Sso;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Features.Onboarding;

public static class InitiateOnboarding
{
    /// <summary>
    /// First-time devices send full <see cref="DeviceInfo"/>; known devices send only <see cref="DeviceId"/>.
    /// </summary>
    public record Request(string EmailAddress, Guid? DeviceId, DeviceDetails? Device);

    private record Response(string EmailAddress, Guid DeviceId, string RetrievalCode, DateTime ExpiresAtUtc);

    public class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(r => r.EmailAddress)
                .NotEmpty()
                .MaximumLength(320)
                .EmailAddress();

            RuleFor(r => r)
                .Must(r => r.DeviceId.HasValue || r.Device is not null)
                .WithMessage("Provide deviceId (known device) and/or device info (first registration).");

            RuleFor(r => r.Device!).SetValidator(new DeviceDetailsValidator())
                .When(r => r.Device is not null);
        }
    }

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/onboarding/initiate", Handle)
                .WithSummary("Start onboarding: verify the email is new (database + SSO) and send an OTP to it")
                .WithTags("Onboarding");
    }

    private static async Task<IResult> Handle(
        Request request,
        AppDbContext db,
        ISsoClient sso,
        IMessageCentre messageCentre,
        IValidator<Request> validator,
        CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ApiResults.ValidationFailed(validation);

        var email = request.EmailAddress.Trim().ToLowerInvariant();

        // 1. The email must not already have a profile in our database.
        if (await db.Profiles.AnyAsync(p => p.EmailAddress == email, ct))
            return ApiResults.Conflict("A profile with this email address already exists.");

        // 2. The email must not already be registered on the SSO.
        bool existsOnSso;
        try
        {
            existsOnSso = await sso.EmailExistsAsync(email, ct);
        }
        catch (HttpRequestException)
        {
            return ApiResults.Fail(StatusCodes.Status503ServiceUnavailable, "Could not verify the email against the SSO service. Try again later.");
        }

        if (existsOnSso)
            return ApiResults.Conflict(
                "This email address is already registered on the SSO. " +
                "Log in with your existing password and resume via POST /api/onboarding/resume.");

        // 3. Resolve the device from whatever was sent: a known id, full device info, or both.
        //    Re-initiating with the same payload simply resends the OTP.
        var device = await DeviceResolver.ResolveAsync(db, request.DeviceId, request.Device, ct);
        if (device is null)
            return ApiResults.Fail(StatusCodes.Status422UnprocessableEntity, $"No device registered with id '{request.DeviceId}'. Send full device info to register it.");

        // 4. Issue the onboarding OTP.
        var now = DateTime.UtcNow;

        var activeCodes = await db.OtpCodes
            .Where(o => o.Target == email &&
                        o.Purpose == OtpPurpose.Onboarding &&
                        o.ConsumedAtUtc == null &&
                        o.ExpiresAtUtc > now)
            .ToListAsync(ct);

        if (activeCodes.Any(o => o.CreatedAtUtc > now.AddSeconds(-Otp.ResendCooldownSeconds)))
            return ApiResults.Fail(StatusCodes.Status429TooManyRequests, $"An OTP was sent recently. Wait {Otp.ResendCooldownSeconds} seconds before requesting another.");

        foreach (var stale in activeCodes)
            stale.ConsumedAtUtc = now;

        var code = Otp.GenerateCode();
        var otp = new OtpCode
        {
            Id = Guid.NewGuid(),
            RetrievalCode = Otp.GenerateRetrievalCode(),
            Purpose = OtpPurpose.Onboarding,
            ProfileId = null,
            // Pre-allocated identity: becomes the SSO SourceId and the profile id at verify-auth.
            SourceId = Guid.NewGuid(),
            DeviceId = device.Id,
            Channel = OtpChannel.Email,
            Target = email,
            CodeHash = Otp.Hash(code, OtpPurpose.Onboarding, email),
            ExpiresAtUtc = now.AddMinutes(Otp.ExpiryMinutes),
            CreatedAtUtc = now
        };
        db.OtpCodes.Add(otp);
        await db.SaveChangesAsync(ct);

        try
        {
            await messageCentre.SendEmailOtpAsync(
                email, code,
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

        return ApiResults.Accepted(new Response(email, device.Id, otp.RetrievalCode, otp.ExpiresAtUtc),
            "OTP sent to your email address.");
    }
}
