using System.Security.Claims;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Common.Sso;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Features.Auth;

public static class PasswordReset
{
    public record Request(Guid DeviceId, string RetrievalCode, string Otp, string NewPassword);

    private record Response(Guid ProfileId, string Message);

    public class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(r => r.DeviceId).NotEmpty();
            RuleFor(r => r.RetrievalCode).NotEmpty().MaximumLength(32);
            RuleFor(r => r.Otp).NotEmpty().Length(Common.Otp.Length).Matches("^[0-9]+$");
            RuleFor(r => r.NewPassword).NotEmpty().MinimumLength(8).MaximumLength(128)
                .Matches("[A-Z]").WithMessage("Password must contain an uppercase letter.")
                .Matches("[a-z]").WithMessage("Password must contain a lowercase letter.")
                .Matches("[0-9]").WithMessage("Password must contain a number.")
                .Matches("[^a-zA-Z0-9]").WithMessage("Password must contain a special character.");
        }
    }

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/auth/password-reset", Handle)
                .RequireAuthorization(TokenTypes.ProfileActivePolicy)
                .WithSummary("Verify the password-reset OTP and set a new password on the SSO")
                .WithTags("Auth");
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

        var profile = await ProfileClaims.ResolveProfileAsync(user, db, ct);
        if (profile is null)
            return ApiResults.Fail(StatusCodes.Status403Forbidden,
                "The token does not resolve to a profile on this service.");

        var activeDeviceId = await db.UserDevices
            .Where(ud => ud.ProfileId == profile.Id && ud.ReleasedAtUtc == null)
            .Select(ud => (Guid?)ud.DeviceId)
            .FirstOrDefaultAsync(ct);
        if (activeDeviceId != request.DeviceId)
            return ApiResults.Fail(StatusCodes.Status403Forbidden,
                "This device is not linked to the profile.");

        var (otp, error) = await OtpFlow.CheckAsync(
            db, request.RetrievalCode, OtpPurpose.PasswordReset, profile.Id,
            profile.EmailAddress, request.Otp,
            "POST /api/auth/initiate-password-reset", ct);
        if (error is not null)
            return error;

        // The SSO username comes from the token (name claim; email as fallback).
        var username = user.FindFirstValue("name") ?? user.FindFirstValue("email") ?? profile.EmailAddress;

        try
        {
            var resetToken = await sso.InitiatePasswordResetAsync(username, ct);
            await sso.ResetPasswordAsync(username, resetToken, request.NewPassword, ct);
        }
        catch (SsoException ex)
        {
            return ApiResults.Fail(StatusCodes.Status502BadGateway, ex.Message);
        }
        catch (HttpRequestException)
        {
            return ApiResults.Fail(StatusCodes.Status503ServiceUnavailable,
                "Could not reach the SSO service. Try again later.");
        }

        ActivityLog.Record(db, ActivityType.PasswordReset, profile.Id, request.DeviceId, "Password reset via OTP.");
        await db.SaveChangesAsync(ct); // consumes the OTP

        return ApiResults.Ok(new Response(profile.Id, "Password reset successfully."),
            "Password reset successfully.");
    }
}
