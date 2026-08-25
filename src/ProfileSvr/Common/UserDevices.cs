using Microsoft.EntityFrameworkCore;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Common;

public static class UserDevices
{
    /// <summary>A device linked less than this many hours ago is "recently linked" — transaction gates apply.</summary>
    public const int RecentLinkHours = 24;

    /// <summary>
    /// Binds a profile to a device exclusively: any other active binding involving either
    /// the profile or the device is released first. Re-binding the same pair is a no-op.
    /// Returns the active binding (its LinkedAtUtc is the gate timestamp).
    /// Does not call SaveChanges — the caller commits.
    /// </summary>
    public static async Task<UserDevice> BindAsync(AppDbContext db, Guid profileId, Guid deviceId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var active = await db.UserDevices
            .Where(ud => ud.ReleasedAtUtc == null &&
                         (ud.ProfileId == profileId || ud.DeviceId == deviceId))
            .ToListAsync(ct);

        var samePair = active.FirstOrDefault(ud => ud.ProfileId == profileId && ud.DeviceId == deviceId);
        if (samePair is not null)
        {
            foreach (var other in active.Where(ud => ud != samePair))
                other.ReleasedAtUtc = now;
            return samePair;
        }

        foreach (var binding in active)
            binding.ReleasedAtUtc = now;

        var created = new UserDevice
        {
            Id = Guid.NewGuid(),
            ProfileId = profileId,
            DeviceId = deviceId,
            LinkedAtUtc = now
        };
        db.UserDevices.Add(created);
        return created;
    }
}
