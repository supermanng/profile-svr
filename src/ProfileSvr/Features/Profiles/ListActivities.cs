using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Database;

namespace ProfileSvr.Features.Profiles;

public static class ListActivities
{
    private record Item(
        Guid Id, Guid ProfileId, string Type, string? Description,
        Guid? DeviceId, string? IpAddress, DateTime CreatedAtUtc);

    private record Response(IReadOnlyList<Item> Items, int Page, int PageSize, int TotalCount);

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/profiles/activities", Handle)
                .RequireAuthorization()
                .WithSummary("List the caller's activity history, newest first (profile from token)")
                .WithTags("Profiles");
    }

    private static async Task<IResult> Handle(
        ClaimsPrincipal user,
        AppDbContext db,
        CancellationToken ct,
        int page = 1,
        int pageSize = 20)
    {
        var profile = await ProfileClaims.ResolveProfileAsync(user, db, ct);
        if (profile is null)
            return ApiResults.Fail(StatusCodes.Status403Forbidden,
                "The token does not resolve to a profile on this service.");

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Activities
            .AsNoTracking()
            .Where(a => a.ProfileId == profile.Id)
            .OrderByDescending(a => a.CreatedAtUtc);

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new Item(
                a.Id, a.ProfileId, a.Type.ToString(), a.Description,
                a.DeviceId, a.IpAddress, a.CreatedAtUtc))
            .ToListAsync(ct);

        return ApiResults.Ok(new Response(items, page, pageSize, total));
    }
}
