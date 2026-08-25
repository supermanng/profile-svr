using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Common;

/// <summary>Device payload shared by onboarding and login.</summary>
public record DeviceDetails(
    string DeviceIdentifier,
    string Name,
    string Platform,
    string? OsName,
    string? OsVersion,
    string? Manufacturer,
    string? Model,
    string? AppVersion);

public class DeviceDetailsValidator : AbstractValidator<DeviceDetails>
{
    public DeviceDetailsValidator()
    {
        RuleFor(d => d.DeviceIdentifier).NotEmpty().MaximumLength(128);
        RuleFor(d => d.Name).NotEmpty().MaximumLength(128);
        RuleFor(d => d.Platform).NotEmpty().MaximumLength(64);
        RuleFor(d => d.OsName).MaximumLength(64);
        RuleFor(d => d.OsVersion).MaximumLength(64);
        RuleFor(d => d.Manufacturer).MaximumLength(128);
        RuleFor(d => d.Model).MaximumLength(128);
        RuleFor(d => d.AppVersion).MaximumLength(32);
    }
}

public static class DeviceResolver
{
    /// <summary>
    /// Resolves a device from an id and/or full details: a valid id wins, details register or
    /// upsert by identifier (refreshing metadata). Returns null when the id is unknown and no
    /// details were sent, or when neither was provided.
    /// </summary>
    public static async Task<Device?> ResolveAsync(
        AppDbContext db, Guid? deviceId, DeviceDetails? info, CancellationToken ct)
    {
        Device? device = null;
        if (deviceId is { } id)
            device = await db.Devices.FindAsync([id], ct);

        if (info is not null)
        {
            var identifier = info.DeviceIdentifier.Trim();
            device ??= await db.Devices.FirstOrDefaultAsync(d => d.DeviceIdentifier == identifier, ct);
            if (device is null)
            {
                device = new Device
                {
                    Id = Guid.NewGuid(),
                    DeviceIdentifier = identifier,
                    CreatedAtUtc = DateTime.UtcNow
                };
                db.Devices.Add(device);
            }
            else
            {
                device.UpdatedAtUtc = DateTime.UtcNow;
            }

            device.Name = info.Name.Trim();
            device.Platform = info.Platform.Trim();
            device.OsName = info.OsName?.Trim();
            device.OsVersion = info.OsVersion?.Trim();
            device.Manufacturer = info.Manufacturer?.Trim();
            device.Model = info.Model?.Trim();
            device.AppVersion = info.AppVersion?.Trim();
        }

        return device;
    }
}
