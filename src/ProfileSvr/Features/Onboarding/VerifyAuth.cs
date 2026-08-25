using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Common.Sso;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Features.Onboarding;

public static class VerifyAuth
{
    public record Request(
        string EmailAddress, Guid DeviceId, string RetrievalCode, string Otp, string Password, string? Username);

    private record TokenBundle(
        string AccessToken, string? RefreshToken, string? IdToken, string TokenType, int ExpiresIn);

    private record Response(
        Guid ProfileId,
        string EmailAddress,
        bool EmailConfirmed,
        string Status,
        string Type,
        Guid? DeviceId,
        DateTime CreatedAtUtc,
        TokenBundle? Tokens);

    public class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(r => r.EmailAddress).NotEmpty().MaximumLength(320).EmailAddress();
            RuleFor(r => r.DeviceId).NotEmpty();
            RuleFor(r => r.RetrievalCode).NotEmpty().MaximumLength(32);
            RuleFor(r => r.Otp).NotEmpty().Length(Common.Otp.Length).Matches("^[0-9]+$");
            RuleFor(r => r.Password).NotEmpty().MinimumLength(8).MaximumLength(128)
                .Matches("[A-Z]").WithMessage("Password must contain an uppercase letter.")
                .Matches("[a-z]").WithMessage("Password must contain a lowercase letter.")
                .Matches("[0-9]").WithMessage("Password must contain a number.")
                .Matches("[^a-zA-Z0-9]").WithMessage("Password must contain a special character.");
            RuleFor(r => r.Username).MaximumLength(128);
        }
    }

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/onboarding/verify-auth", Handle)
                .WithSummary("Verify the email OTP and create the SSO auth (profile stays pending until create-profile)")
                .WithTags("Onboarding");
    }

    private static async Task<IResult> Handle(
        Request request,
        AppDbContext db,
        ISsoClient sso,
        IValidator<Request> validator,
        CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ApiResults.ValidationFailed(validation);

        var email = request.EmailAddress.Trim().ToLowerInvariant();
        var now = DateTime.UtcNow;

        var otp = await db.OtpCodes
            .FirstOrDefaultAsync(o => o.RetrievalCode == request.RetrievalCode, ct);

        if (otp is null || otp.ConsumedAtUtc is not null || otp.ExpiresAtUtc <= now)
            return ApiResults.Fail(StatusCodes.Status400BadRequest, "No active OTP matches this retrieval code. Start over via POST /api/onboarding/initiate.");

        // The OTP must have been issued for this exact purpose, email and device.
        if (otp.Purpose != OtpPurpose.Onboarding)
            return ApiResults.Fail(StatusCodes.Status400BadRequest, "This OTP was not issued for onboarding.");

        if (!string.Equals(otp.Target, email, StringComparison.OrdinalIgnoreCase))
            return ApiResults.Fail(StatusCodes.Status400BadRequest, "This OTP was not issued for this email address.");

        if (otp.DeviceId != request.DeviceId)
            return ApiResults.Fail(StatusCodes.Status400BadRequest, "The OTP was issued to a different device.");

        if (otp.Attempts >= Otp.MaxAttempts)
        {
            otp.ConsumedAtUtc = now;
            await db.SaveChangesAsync(ct);
            return ApiResults.Fail(StatusCodes.Status429TooManyRequests, "This code has been locked after too many failed attempts. Request a new one.");
        }

        if (!Otp.Verify(request.Otp, OtpPurpose.Onboarding, email, otp.CodeHash))
        {
            otp.Attempts++;
            await db.SaveChangesAsync(ct);
            return ApiResults.Fail(StatusCodes.Status400BadRequest, "The code is incorrect.");
        }

        // Guard against a profile created since initiation.
        if (await db.Profiles.AnyAsync(p => p.EmailAddress == email, ct))
            return ApiResults.Conflict("A profile with this email address already exists.");

        // The identity pre-allocated at initiation: SSO SourceId == profile id, always.
        var profileId = otp.SourceId ?? Guid.NewGuid();

        // Create the SSO user first — if it fails, nothing is persisted locally.
        try
        {
            await sso.CreateUserAsync(
                username: string.IsNullOrWhiteSpace(request.Username) ? email : request.Username.Trim(),
                password: request.Password,
                email: email,
                sourceId: profileId.ToString(),
                ct);
        }
        catch (SsoException ex)
        {
            return ApiResults.Fail(StatusCodes.Status502BadGateway, ex.Message);
        }
        catch (HttpRequestException)
        {
            return ApiResults.Fail(StatusCodes.Status503ServiceUnavailable, "Could not reach the SSO service. Try again later.");
        }

        var profile = new Profile
        {
            Id = profileId,
            Status = ProfileStatus.AuthCreated,
            EmailAddress = email,
            PhoneNumber = null,
            EmailConfirmed = true,
            PhoneNumberConfirmed = false,
            DeviceId = otp.DeviceId,
            CreatedAtUtc = now
        };
        db.Profiles.Add(profile);

        otp.ConsumedAtUtc = now;
        otp.ProfileId = profileId;

        // The new profile is exclusively bound to the device it onboarded from.
        if (otp.DeviceId is { } boundDeviceId)
            await UserDevices.BindAsync(db, profileId, boundDeviceId, ct);

        ActivityLog.Record(db, ActivityType.AuthCreated, profileId, otp.DeviceId, "SSO auth created; email verified.");
        ActivityLog.Record(db, ActivityType.EmailVerified, profileId, otp.DeviceId, $"Email {email} verified.");
        await db.SaveChangesAsync(ct);

        // Log the new auth in so onboarding continues without a separate login call.
        // The token's derived type claim is "onboarding" while the profile stays AuthCreated.
        TokenBundle? tokenBundle = null;
        try
        {
            var username = string.IsNullOrWhiteSpace(request.Username) ? email : request.Username.Trim();
            // Stamp the type claim (service token) BEFORE minting the token, so it carries type=onboarding.
            await sso.SetTypeClaimAsync(username, TokenTypes.Onboarding, ct);
            var tokens = await sso.PasswordLoginAsync(username, request.Password, ct);
            tokenBundle = new TokenBundle(
                tokens.AccessToken, tokens.RefreshToken, tokens.IdToken, tokens.TokenType, tokens.ExpiresIn);
        }
        catch (Exception ex) when (ex is SsoException or HttpRequestException)
        {
            // Auth + profile are committed — the client can still log in explicitly.
        }

        var response = new Response(
            profile.Id, profile.EmailAddress, profile.EmailConfirmed,
            profile.Status.ToString(), TokenTypes.Onboarding, profile.DeviceId,
            profile.CreatedAtUtc, tokenBundle);

        return ApiResults.Created($"/api/profiles/{profile.Id}", response,
            "Email verified and your login has been created. Continue with phone verification.");
    }
}
