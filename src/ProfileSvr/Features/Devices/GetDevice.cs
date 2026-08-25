using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Database;

namespace ProfileSvr.Features.Devices;

public static class GetDevice
{
    private record Response(
        Guid Id,
        string DeviceIdentifier,
        string Name,
        string Platform,
        string? OsName,
        string? OsVersion,
        string? Manufacturer,
        string? Model,
        string? AppVersion,
        DateTime CreatedAtUtc,
        DateTime? UpdatedAtUtc);

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapGet("/api/devices/{id:guid}", Handle)
                .WithSummary("Get a device by id")
                .WithTags("Devices");
    }

    private static async Task<IResult> Handle(Guid id, AppDbContext db, CancellationToken ct)
    {
        var device = await db.Devices
            .AsNoTracking()
            .Where(d => d.Id == id)
            .Select(d => new Response(
                d.Id, d.DeviceIdentifier, d.Name, d.Platform,
                d.OsName, d.OsVersion, d.Manufacturer, d.Model,
                d.AppVersion, d.CreatedAtUtc, d.UpdatedAtUtc))
            .FirstOrDefaultAsync(ct);

        return device is null ? ApiResults.NotFound("Device not found.") : ApiResults.Ok(device);
    }
}
