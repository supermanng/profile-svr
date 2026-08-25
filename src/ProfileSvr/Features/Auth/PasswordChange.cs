using System.Security.Claims;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Common.Sso;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Features.Auth;

public static class PasswordChange
{
    public record Request(Guid DeviceId, string CurrentPassword, string NewPassword);

    private record Response(Guid ProfileId, string Message);

    public class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(r => r.DeviceId).NotEmpty();
            RuleFor(r => r.CurrentPassword).NotEmpty();
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
            app.MapPost("/api/auth/password-change", Handle)
                .RequireAuthorization(TokenTypes.ProfileActivePolicy)
                .WithSummary("Change the caller's password on the SSO using the current one")
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

        var username = user.FindFirstValue("name") ?? user.FindFirstValue("email") ?? profile.EmailAddress;

        try
        {
            await sso.ChangePasswordAsync(username, request.CurrentPassword, request.NewPassword, ct);
        }
        catch (SsoException ex)
        {
            return ApiResults.Fail(StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (HttpRequestException)
        {
            return ApiResults.Fail(StatusCodes.Status503ServiceUnavailable,
                "Could not reach the SSO service. Try again later.");
        }

        ActivityLog.Record(db, ActivityType.PasswordChanged, profile.Id, request.DeviceId, "Password changed.");
        await db.SaveChangesAsync(ct);

        return ApiResults.Ok(new Response(profile.Id, "Password changed successfully."),
            "Password changed successfully.");
    }
}
