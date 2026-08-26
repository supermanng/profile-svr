using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Common.Accounts;
using ProfileSvr.Common.Sso;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Features.Auth;

public static class Login
{
    /// <summary>
    /// Username (or email) + password, plus the id of the device logging in.
    /// Devices are registered during onboarding — login only accepts a known deviceId.
    /// </summary>
    public record Request(string Username, string Password, Guid DeviceId);

    /// <summary>
    /// New — this login linked the device to the profile for the first time;
    /// Existing — the device was already the profile's active device;
    /// Unlinked — no profile resolved, so no binding is involved.
    /// Accounts and Rates come from the core-banking provider (as in vliquidity): missing
    /// accounts for a verified profile are re-provisioned here, and provider failures
    /// degrade to empty lists rather than failing the login.
    /// </summary>
    private record Response(
        string AccessToken,
        string? RefreshToken,
        string? IdToken,
        string TokenType,
        int ExpiresIn,
        string DeviceStatus,
        string Type,
        ProfileDto? Profile,
        IReadOnlyList<AccountDetail>? Accounts,
        IReadOnlyList<CurrencyRate>? Rates);

    public class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(r => r.Username).NotEmpty().MaximumLength(320);
            RuleFor(r => r.Password).NotEmpty().MaximumLength(128);
            RuleFor(r => r.DeviceId).NotEmpty();
        }
    }

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/auth/login", Handle)
                .WithSummary("Authenticate against the SSO (password grant) and register the device used")
                .WithTags("Auth");
    }

    private static async Task<IResult> Handle(
        Request request,
        AppDbContext db,
        ISsoClient sso,
        IAccountFacade accountFacade,
        IValidator<Request> validator,
        CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ApiResults.ValidationFailed(validation);

        // The device must have been registered during onboarding.
        var device = await db.Devices.FindAsync([request.DeviceId], ct);
        if (device is null)
            return ApiResults.Fail(StatusCodes.Status422UnprocessableEntity,
                $"No device registered with id '{request.DeviceId}'.");

        SsoTokens tokens;
        try
        {
            tokens = await sso.PasswordLoginAsync(request.Username.Trim(), request.Password, ct);
        }
        catch (SsoException ex)
        {
            return ApiResults.Fail(StatusCodes.Status401Unauthorized, ex.Message);
        }
        catch (HttpRequestException)
        {
            return ApiResults.Fail(StatusCodes.Status503ServiceUnavailable,
                "Could not reach the SSO service. Try again later.");
        }

        // Record the sign-in on the device.
        device.UpdatedAtUtc = DateTime.UtcNow;

        // Resolve the profile (when the username is the profile email). A profile bound to a
        // different device may not sign in from this one — the device-change OTP flow moves
        // the binding. A profile with no active binding gets bound to this device.
        var username = request.Username.Trim().ToLowerInvariant();
        var profile = await db.Profiles.FirstOrDefaultAsync(p => p.EmailAddress == username, ct);

        ProfileDto? profileDto = null;
        IReadOnlyList<AccountDetail>? accounts = null;
        IReadOnlyList<CurrencyRate>? rates = null;
        var deviceStatus = "Unlinked";
        if (profile is not null)
        {
            var binding = await db.UserDevices
                .FirstOrDefaultAsync(ud => ud.ProfileId == profile.Id && ud.ReleasedAtUtc == null, ct);

            if (binding is not null && binding.DeviceId != device.Id)
                return ApiResults.Fail(StatusCodes.Status403Forbidden,
                    "This profile is linked to a different device. Approve the device change first via " +
                    "POST /api/profiles/{id}/request-device-change and /api/profiles/{id}/change-device.");

            var isNewBinding = binding is null;
            binding ??= await UserDevices.BindAsync(db, profile.Id, device.Id, ct);
            deviceStatus = isNewBinding ? "New" : "Existing";

            ActivityLog.Record(db, ActivityType.LoggedIn, profile.Id, device.Id, "Signed in.");
            await db.SaveChangesAsync(ct);

            // Retry any missing account provisioning, then load accounts + rates (best-effort).
            (accounts, rates) = await AccountSession.LoadAsync(db, accountFacade, profile, ct);

            profileDto = ProfileDtoMapper.Build(profile, binding.DeviceId, binding.LinkedAtUtc);
        }
        else
        {
            await db.SaveChangesAsync(ct);
        }

        return ApiResults.Ok(new Response(
            tokens.AccessToken,
            tokens.RefreshToken,
            tokens.IdToken,
            tokens.TokenType,
            tokens.ExpiresIn,
            deviceStatus,
            TokenTypes.ForProfile(profile),
            profileDto,
            accounts,
            rates),
            "Login successful.");
    }
}
