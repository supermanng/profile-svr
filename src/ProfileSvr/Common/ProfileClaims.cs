using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Common;

public static class ProfileClaims
{
    /// <summary>
    /// Resolves the caller's profile from the bearer token: the SSO's SourceId claim
    /// carries the profile id (set at user creation), with the email claim as fallback.
    /// </summary>
    public static async Task<Profile?> ResolveProfileAsync(
        ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        if (Guid.TryParse(user.FindFirstValue("SourceId"), out var profileId))
        {
            var byId = await db.Profiles.FindAsync([profileId], ct);
            if (byId is not null)
                return byId;
        }

        var email = user.FindFirstValue("email")?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(email))
            return await db.Profiles.FirstOrDefaultAsync(p => p.EmailAddress == email, ct);

        return null;
    }
}
