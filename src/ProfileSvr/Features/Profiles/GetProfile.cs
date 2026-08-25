using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Database;

namespace ProfileSvr.Features.Profiles;

public static class GetProfile
{
    public record Response(
        Guid Id,
        string Status,
        string EmailAddress,
        string? PhoneNumber,
        bool EmailConfirmed,
        bool PhoneNumberConfirmed,
        string? FirstName,
        string? LastName,
        string? MiddleName,
        DateOnly? DateOfBirth,
        string? Gender,
        int Tier,
        string? Cif,
        string? Address,
        string? Bvn,
        string? Nin,
        bool BvnIsVerified,
        bool NinIsVerified,
        bool HasSetTransactionPin,
        Guid? DeviceId,
        Guid? ActiveDeviceId,
        DateTime? ActiveDeviceLinkedAtUtc,
        bool DeviceRecentlyLinked,
        DateTime? DeviceChangedAtUtc,
        bool DeviceRecentlyChanged,
        DateTime CreatedAtUtc,
        DateTime? UpdatedAtUtc);

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/profiles/{id:guid}", Handle)
                .WithSummary("Get a profile by id")
                .WithTags("Profiles");
    }

    private static async Task<IResult> Handle(Guid id, AppDbContext db, CancellationToken ct)
    {
        var recentCutoff = DateTime.UtcNow.AddHours(-24);
        var profile = await db.Profiles
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new Response(
                p.Id,
                p.Status.ToString(),
                p.EmailAddress,
                p.PhoneNumber,
                p.EmailConfirmed,
                p.PhoneNumberConfirmed,
                p.FirstName,
                p.LastName,
                p.MiddleName,
                p.DateOfBirth,
                p.Gender,
                p.Tier,
                p.Cif,
                p.Address,
                p.Bvn == null ? null : "*******" + p.Bvn.Substring(p.Bvn.Length - 4),
                p.Nin == null ? null : "*******" + p.Nin.Substring(p.Nin.Length - 4),
                p.BvnIsVerified,
                p.NinIsVerified,
                p.HasSetTransactionPin,
                p.DeviceId,
                db.UserDevices
                    .Where(ud => ud.ProfileId == p.Id && ud.ReleasedAtUtc == null)
                    .Select(ud => (Guid?)ud.DeviceId)
                    .FirstOrDefault(),
                db.UserDevices
                    .Where(ud => ud.ProfileId == p.Id && ud.ReleasedAtUtc == null)
                    .Select(ud => (DateTime?)ud.LinkedAtUtc)
                    .FirstOrDefault(),
                db.UserDevices.Any(ud => ud.ProfileId == p.Id &&
                    ud.ReleasedAtUtc == null && ud.LinkedAtUtc > recentCutoff),
                p.DeviceChangedAtUtc,
                p.DeviceChangedAtUtc != null && p.DeviceChangedAtUtc > recentCutoff,
                p.CreatedAtUtc,
                p.UpdatedAtUtc))
            .FirstOrDefaultAsync(ct);

        return profile is null ? ApiResults.NotFound("Profile not found.") : ApiResults.Ok(profile);
    }
}
