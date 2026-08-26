using System.Security.Claims;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Common.Accounts;
using ProfileSvr.Common.Sso;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Features.Onboarding;

/// <summary>
/// Final onboarding step ("Tell Us About You"): personal details are added to the profile.
/// The profile is identified by the bearer token — the SSO puts the profile id in the
/// SourceId claim (set at user creation), with the email claim as fallback.
/// Requires both the email and the phone number to be verified.
/// Accounts are generated at complete-kyc (the provider needs a verified BVN/NIN); this step
/// only retries any provisioning KYC missed — as do login and token refresh.
/// </summary>
public static class CreateProfile
{
    public record Request(
        Guid DeviceId,
        string FirstName,
        string LastName,
        string? MiddleName,
        DateOnly DateOfBirth);

    private record Response(
        Guid ProfileId,
        string FirstName,
        string LastName,
        string? MiddleName,
        DateOnly DateOfBirth,
        string Status,
        string KycStatus,
        string? Cif,
        string? NairaAccount,
        string? CadAccount,
        string? VirtualAccount,
        string? VirtualAccountBank,
        DateTime? UpdatedAtUtc);

    public class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(r => r.DeviceId).NotEmpty();
            RuleFor(r => r.FirstName).NotEmpty().MaximumLength(100);
            RuleFor(r => r.LastName).NotEmpty().MaximumLength(100);
            RuleFor(r => r.MiddleName).MaximumLength(100);
            RuleFor(r => r.DateOfBirth)
                .Must(dob => dob < DateOnly.FromDateTime(DateTime.UtcNow) &&
                             dob > new DateOnly(1900, 1, 1))
                .WithMessage("Date of birth must be a valid past date.");
        }
    }

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/onboarding/create-profile", Handle)
                .RequireAuthorization(TokenTypes.OnboardingPolicy)
                .WithSummary("Create the profile: personal details + activation (email and phone must be verified; token identifies it)")
                .WithTags("Onboarding");
    }

    private static async Task<IResult> Handle(
        Request request,
        ClaimsPrincipal user,
        AppDbContext db,
        ISsoClient sso,
        IAccountFacade accounts,
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

        // The request must come from the device the profile is actively bound to.
        var activeDeviceId = await db.UserDevices
            .Where(ud => ud.ProfileId == profile.Id && ud.ReleasedAtUtc == null)
            .Select(ud => (Guid?)ud.DeviceId)
            .FirstOrDefaultAsync(ct);
        if (activeDeviceId != request.DeviceId)
            return ApiResults.Fail(StatusCodes.Status403Forbidden,
                "This device is not linked to the profile.");

        if (!profile.EmailConfirmed)
            return ApiResults.Fail(StatusCodes.Status422UnprocessableEntity,
                "The email address is not confirmed yet.");

        if (!profile.PhoneNumberConfirmed)
            return ApiResults.Fail(StatusCodes.Status422UnprocessableEntity,
                "The phone number is not confirmed yet. Complete POST /api/onboarding/verify-phone first.");

        profile.FirstName = request.FirstName.Trim();
        profile.LastName = request.LastName.Trim();
        profile.MiddleName = string.IsNullOrWhiteSpace(request.MiddleName) ? null : request.MiddleName.Trim();
        profile.DateOfBirth = request.DateOfBirth;
        profile.Status = ProfileStatus.Active;
        profile.UpdatedAtUtc = DateTime.UtcNow;
        ActivityLog.Record(db, ActivityType.ProfileCreated, profile.Id, request.DeviceId, "Profile created and activated.");
        await db.SaveChangesAsync(ct);

        // Flip the SSO type claim so freshly issued tokens carry profile-active.
        // Best-effort: our per-request derivation is authoritative regardless.
        try
        {
            var username = user.FindFirstValue("name") ?? profile.EmailAddress;
            await sso.SetTypeClaimAsync(username, TokenTypes.ProfileActive, ct);
        }
        catch (Exception ex) when (ex is SsoException or HttpRequestException)
        {
        }

        // Accounts are generated at complete-kyc (the provider requires a verified BVN/NIN);
        // this is a retry point for anything that step missed. Pre-KYC profiles no-op here
        // and get their accounts when KYC completes.
        if (await accounts.EnsureAccountsAsync(profile, ct))
        {
            ActivityLog.Record(db, ActivityType.AccountsProvisioned, profile.Id, request.DeviceId,
                $"Banking accounts provisioned (cif: {profile.Cif ?? "-"}, " +
                $"NGN: {profile.NairaAccount ?? "-"}, CAD: {profile.CadAccount ?? "-"}, " +
                $"virtual: {profile.VirtualAccount ?? "-"}).");
            profile.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return ApiResults.Ok(new Response(
            profile.Id, profile.FirstName, profile.LastName, profile.MiddleName,
            request.DateOfBirth, profile.Status.ToString(), profile.KycStatus.ToString(),
            profile.Cif, profile.NairaAccount, profile.CadAccount,
            profile.VirtualAccount, profile.VirtualAccountBank, profile.UpdatedAtUtc),
            "Profile created successfully.");
    }

}
