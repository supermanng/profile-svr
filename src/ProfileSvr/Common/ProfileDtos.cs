using Microsoft.EntityFrameworkCore;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Common;

/// <summary>Full profile snapshot returned by login and /api/auth/me.</summary>
public record ProfileDto(
    Guid ProfileId,
    string Status,
    string EmailAddress,
    string? PhoneNumber,
    string? FirstName,
    string? LastName,
    string? MiddleName,
    string? Gender,
    DateOnly? DateOfBirth,
    int Tier,
    string? Cif,
    string? NairaAccount,
    string? CadAccount,
    string? VirtualAccount,
    string? VirtualAccountBank,
    string KycStatus,
    string? KycStatusReason,
    string? Address,
    string? Bvn,
    string? Nin,
    bool EmailConfirmed,
    bool PhoneNumberConfirmed,
    bool BvnIsVerified,
    bool NinIsVerified,
    bool HasSetTransactionPin,
    bool ProfileCompleted,
    Guid? ActiveDeviceId,
    DateTime? ActiveDeviceLinkedAtUtc,
    bool DeviceRecentlyLinked,
    DateTime? DeviceChangedAtUtc,
    bool DeviceRecentlyChanged,
    DateTime CreatedAtUtc);

public static class ProfileDtoMapper
{
    public static async Task<ProfileDto> BuildAsync(AppDbContext db, Profile profile, CancellationToken ct)
    {
        var binding = await db.UserDevices
            .Where(ud => ud.ProfileId == profile.Id && ud.ReleasedAtUtc == null)
            .Select(ud => new { ud.DeviceId, ud.LinkedAtUtc })
            .FirstOrDefaultAsync(ct);

        return Build(profile, binding?.DeviceId, binding?.LinkedAtUtc);
    }

    public static ProfileDto Build(Profile profile, Guid? activeDeviceId, DateTime? activeDeviceLinkedAtUtc) => new(
        profile.Id,
        profile.Status.ToString(),
        profile.EmailAddress,
        profile.PhoneNumber,
        profile.FirstName,
        profile.LastName,
        profile.MiddleName,
        profile.Gender,
        profile.DateOfBirth,
        profile.Tier,
        profile.Cif,
        profile.NairaAccount,
        profile.CadAccount,
        profile.VirtualAccount,
        profile.VirtualAccountBank,
        profile.KycStatus.ToString(),
        profile.KycStatusReason,
        profile.Address,
        profile.Bvn,
        profile.Nin,
        profile.EmailConfirmed,
        profile.PhoneNumberConfirmed,
        profile.BvnIsVerified,
        profile.NinIsVerified,
        profile.HasSetTransactionPin,
        ProfileCompleted: profile.FirstName is not null && profile.DateOfBirth is not null,
        activeDeviceId,
        activeDeviceLinkedAtUtc,
        activeDeviceLinkedAtUtc is { } linked && DateTime.UtcNow - linked < TimeSpan.FromHours(UserDevices.RecentLinkHours),
        profile.DeviceChangedAtUtc,
        profile.DeviceRecentlyChanged,
        profile.CreatedAtUtc);
}
