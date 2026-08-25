using System.Security.Claims;
using FluentValidation;
using ProfileSvr.Common;
using ProfileSvr.Common.Sso;
using ProfileSvr.Database;
using ProfileSvr.Domain;
using Microsoft.EntityFrameworkCore;

namespace ProfileSvr.Features.Onboarding;

/// <summary>
/// Completes an onboarding resume for an SSO account with no record here: verifies the
/// email OTP issued by initiate-resume, registers the device (this is the only way a
/// resume device gets registered — the OTP is its approval), creates the staging record
/// (status AuthCreated, email confirmed) and binds the device. Onboarding then continues
/// normally (initiate-phone → verify → create-profile).
/// </summary>
public static class ResumeOnboarding
{
    /// <summary>The device: full details (new device) and/or a known deviceId.</summary>
    public record Request(string RetrievalCode, string Otp, Guid? DeviceId, DeviceDetails? Device);

    private record Response(Guid ProfileId, string EmailAddress, string Status, Guid DeviceId, bool Resumed);

    public class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(r => r.RetrievalCode).NotEmpty().MaximumLength(32);
            RuleFor(r => r.Otp).NotEmpty().Length(Common.Otp.Length).Matches("^[0-9]+$");
            RuleFor(r => r)
                .Must(r => r.DeviceId.HasValue || r.Device is not null)
                .WithMessage("Provide deviceId (known device) and/or device info (new device).");
            RuleFor(r => r.Device!).SetValidator(new DeviceDetailsValidator())
                .When(r => r.Device is not null);
        }
    }

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/onboarding/resume", Handle)
                .RequireAuthorization()
                .WithSummary("Verify the resume OTP, register the device and recreate the onboarding record")
                .WithTags("Onboarding");
    }

    private static async Task<IResult> Handle(
        Request request,
        ClaimsPrincipal user,
        AppDbContext db,
        ISsoClient sso,
        IValidator<Request> validator,
        CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ApiResults.ValidationFailed(validation);

        var email = user.FindFirstValue("email")?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email))
            return ApiResults.Fail(StatusCodes.Status400BadRequest,
                "The token carries no email claim to resume with.");

        // Idempotent: a record that already exists needs no recovery.
        var existing = await ProfileClaims.ResolveProfileAsync(user, db, ct);
        if (existing is not null)
        {
            var boundDevice = await db.UserDevices
                .Where(ud => ud.ProfileId == existing.Id && ud.ReleasedAtUtc == null)
                .Select(ud => ud.DeviceId)
                .FirstOrDefaultAsync(ct);
            return ApiResults.Ok(new Response(
                existing.Id, existing.EmailAddress, existing.Status.ToString(), boundDevice, Resumed: false),
                "A profile already exists for this account.");
        }

        // Verify the device-registration OTP sent to the token's email.
        var now = DateTime.UtcNow;
        var otp = await db.OtpCodes.FirstOrDefaultAsync(o => o.RetrievalCode == request.RetrievalCode, ct);

        if (otp is null || otp.ConsumedAtUtc is not null || otp.ExpiresAtUtc <= now)
            return ApiResults.Fail(StatusCodes.Status400BadRequest,
                "No active OTP matches this retrieval code. Request one via POST /api/onboarding/initiate-resume.");

        if (otp.Purpose != OtpPurpose.DeviceRegistration)
            return ApiResults.Fail(StatusCodes.Status400BadRequest,
                "This OTP was not issued for device registration.");

        if (!string.Equals(otp.Target, email, StringComparison.OrdinalIgnoreCase))
            return ApiResults.Fail(StatusCodes.Status400BadRequest,
                "This OTP was not issued for this account.");

        if (otp.Attempts >= Otp.MaxAttempts)
        {
            otp.ConsumedAtUtc = now;
            await db.SaveChangesAsync(ct);
            return ApiResults.Fail(StatusCodes.Status429TooManyRequests,
                "This code has been locked after too many failed attempts. Request a new one.");
        }

        if (!Otp.Verify(request.Otp, OtpPurpose.DeviceRegistration, email, otp.CodeHash))
        {
            otp.Attempts++;
            await db.SaveChangesAsync(ct);
            return ApiResults.Fail(StatusCodes.Status400BadRequest, "The code is incorrect.");
        }

        // OTP approved — register (or resolve) the device now.
        var device = await DeviceResolver.ResolveAsync(db, request.DeviceId, request.Device, ct);
        if (device is null)
            return ApiResults.Fail(StatusCodes.Status422UnprocessableEntity,
                $"No device registered with id '{request.DeviceId}'. Send full device info to register it.");

        // A device actively in use by another profile cannot be claimed through resume —
        // only that profile's own device-change flow may release it.
        var inUseElsewhere = await db.UserDevices
            .AnyAsync(ud => ud.DeviceId == device.Id && ud.ReleasedAtUtc == null, ct);
        if (inUseElsewhere)
            return ApiResults.Conflict(
                "This device is currently in use by another profile. Resume with a different device, " +
                "or have that profile release it via its device-change flow.");

        // Restore the original identity: the token's SourceId claim was minted at initiation
        // and stored on the SSO user, so the recreated profile reclaims that exact id.
        // (ResolveProfileAsync returned null above, so no row occupies it.)
        var profileId = Guid.TryParse(user.FindFirstValue("SourceId"), out var sourceId)
            ? sourceId
            : Guid.NewGuid();

        var profile = new Profile
        {
            Id = profileId,
            Status = ProfileStatus.AuthCreated,
            EmailAddress = email,
            EmailConfirmed = true,
            PhoneNumberConfirmed = false,
            DeviceId = device.Id,
            CreatedAtUtc = now
        };
        db.Profiles.Add(profile);

        otp.ConsumedAtUtc = now;
        otp.ProfileId = profile.Id;
        otp.DeviceId = device.Id;

        await UserDevices.BindAsync(db, profile.Id, device.Id, ct);

        ActivityLog.Record(db, ActivityType.AuthCreated, profile.Id, device.Id,
            "Onboarding resumed for an existing SSO account; device approved via OTP.");
        ActivityLog.Record(db, ActivityType.EmailVerified, profile.Id, device.Id,
            $"Email {email} verified via resume OTP.");
        await db.SaveChangesAsync(ct);

        try
        {
            var username = user.FindFirstValue("name") ?? email;
            await sso.SetTypeClaimAsync(username, TokenTypes.Onboarding, ct);
        }
        catch (Exception ex) when (ex is SsoException or HttpRequestException)
        {
        }

        return ApiResults.Created($"/api/profiles/{profile.Id}", new Response(
            profile.Id, profile.EmailAddress, profile.Status.ToString(), device.Id, Resumed: true),
            "Onboarding resumed. Continue with phone verification.");
    }
}
