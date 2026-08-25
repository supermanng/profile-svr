using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Database;

namespace ProfileSvr.Features.Profiles;

public static class ListProfiles
{
    public record Item(
        Guid Id,
        string EmailAddress,
        string? PhoneNumber,
        bool EmailConfirmed,
        bool PhoneNumberConfirmed,
        Guid? DeviceId,
        DateTime CreatedAtUtc);

    public record Response(IReadOnlyList<Item> Items, int Page, int PageSize, int TotalCount);

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/profiles", Handle)
                .WithSummary("List profiles (paged)")
                .WithTags("Profiles");
    }

    private static async Task<IResult> Handle(
        AppDbContext db,
        CancellationToken ct,
        int page = 1,
        int pageSize = 20)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Profiles.AsNoTracking().OrderBy(p => p.CreatedAtUtc);

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new Item(
                p.Id,
                p.EmailAddress,
                p.PhoneNumber,
                p.EmailConfirmed,
                p.PhoneNumberConfirmed,
                p.DeviceId,
                p.CreatedAtUtc))
            .ToListAsync(ct);

        return ApiResults.Ok(new Response(items, page, pageSize, total));
    }
}
