using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Database;

namespace ProfileSvr.Features.Profiles;

/// <summary>
/// The caller's devices over time: every device their profile has ever been bound to,
/// with exactly one marked in use (isActive) at any moment. The profile is resolved
/// from the bearer token, never from the URL.
/// </summary>
public static class ListProfileDevices
{
    private record Item(
        Guid DeviceId,
        string Name,
        string Platform,
        string? Model,
        bool IsActive,
        DateTime LinkedAtUtc,
        DateTime? ReleasedAtUtc);

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/profiles/devices", Handle)
                .RequireAuthorization()
                .WithSummary("List the caller's devices — one active (in use), the rest history (profile from token)")
                .WithTags("Profiles");
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

        var items = await db.UserDevices
            .AsNoTracking()
            .Where(ud => ud.ProfileId == profile.Id)
            .OrderByDescending(ud => ud.ReleasedAtUtc == null)
            .ThenByDescending(ud => ud.LinkedAtUtc)
            .Select(ud => new Item(
                ud.DeviceId,
                ud.Device!.Name,
                ud.Device.Platform,
                ud.Device.Model,
                ud.ReleasedAtUtc == null,
                ud.LinkedAtUtc,
                ud.ReleasedAtUtc))
            .ToListAsync(ct);

        return ApiResults.Ok(items);
    }
}
