using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Common;

public static class ActivityLog
{
    /// <summary>Set once at startup so Record can capture the caller's IP automatically.</summary>
    public static IHttpContextAccessor? HttpContextAccessor { get; set; }

    /// <summary>Queues an activity row (with the client IP). Persisted by the caller's SaveChanges.</summary>
    public static void Record(
        AppDbContext db, ActivityType type, Guid profileId,
        Guid? deviceId = null, string? description = null) =>
        db.Activities.Add(new Activity
        {
            Id = Guid.NewGuid(),
            ProfileId = profileId,
            DeviceId = deviceId,
            Type = type,
            Description = description,
            IpAddress = ClientIp(),
            CreatedAtUtc = DateTime.UtcNow
        });

    private static string? ClientIp()
    {
        var context = HttpContextAccessor?.HttpContext;
        if (context is null)
            return null;

        // Behind a proxy/load balancer the original client is the first X-Forwarded-For entry.
        var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwarded))
            return forwarded.Split(',')[0].Trim();

        return context.Connection.RemoteIpAddress?.ToString();
    }
}
