using System.Security.Claims;
using ProfileSvr.Common;
using ProfileSvr.Database;

namespace ProfileSvr.Features.Auth;

/// <summary>
/// Reloads the caller's profile from a valid bearer token — same DTO as login,
/// without re-entering the password. Use /api/auth/refresh when the token itself expires.
/// </summary>
public static class Me
{
    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/auth/me", Handle)
                .RequireAuthorization()
                .WithSummary("Reload the caller's profile from the bearer token (no password needed)")
                .WithTags("Auth");
    }

    private static async Task<IResult> Handle(
        ClaimsPrincipal user,
        AppDbContext db,
        CancellationToken ct)
    {
        var profile = await ProfileClaims.ResolveProfileAsync(user, db, ct);
        if (profile is null)
            return ApiResults.Fail(StatusCodes.Status403Forbidden,
                "The token does not resolve to a profile on this service.");

        return ApiResults.Ok(await ProfileDtoMapper.BuildAsync(db, profile, ct));
    }
}
