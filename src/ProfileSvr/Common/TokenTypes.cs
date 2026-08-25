using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Common;

/// <summary>
/// The "type" claim derived for every authenticated request:
///   temporary      — token authenticates but no profile record exists (resume bootstrap only)
///   onboarding     — profile is in AuthCreated: valid for the onboarding process alone
///   profile-active — profile is Active: valid for everything except onboarding
/// </summary>
public static class TokenTypes
{
    public const string ClaimName = "type";
    public const string Temporary = "temporary";
    public const string Onboarding = "onboarding";
    public const string ProfileActive = "profile-active";

    public const string OnboardingPolicy = "OnboardingOnly";
    public const string ProfileActivePolicy = "ProfileActive";

    public static string ForProfile(Profile? profile) =>
        profile is null ? Temporary
        : profile.Status == ProfileStatus.Active ? ProfileActive
        : Onboarding;
}

/// <summary>Enriches every authenticated principal with the derived "type" claim.</summary>
public class ProfileTypeClaimsTransformation(AppDbContext db) : IClaimsTransformation
{
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
            return principal;

        // The live profile status wins over whatever type the (possibly stale) JWT carries.
        var profile = await ProfileClaims.ResolveProfileAsync(principal, db, CancellationToken.None);
        var identity = (ClaimsIdentity)principal.Identity;
        foreach (var stale in identity.FindAll(TokenTypes.ClaimName).ToList())
            identity.RemoveClaim(stale);
        identity.AddClaim(new Claim(TokenTypes.ClaimName, TokenTypes.ForProfile(profile)));
        return principal;
    }
}
