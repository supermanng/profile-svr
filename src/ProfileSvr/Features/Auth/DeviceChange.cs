using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Features.Auth;

/// <summary>
/// Completes a device change: verifies the email OTP issued by initiate-device-change,
/// registers/resolves the new device (a locked-out user's device is usually unregistered)
/// and moves the profile's exclusive binding to it. The user can then log in from it.
/// </summary>
public static class DeviceChange
{
    /// <summary>The new device: full details (unregistered) and/or a known deviceId.</summary>
    public record Request(string Username, string RetrievalCode, string Otp, Guid? DeviceId, DeviceDetails? Device);

    private record Response(
        Guid DeviceId,
        DateTime DeviceChangedAtUtc,
        bool DeviceRecentlyLinked);

    public class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(r => r.Username).NotEmpty().MaximumLength(320);
            RuleFor(r => r.RetrievalCode).NotEmpty().MaximumLength(32);
            RuleFor(r => r.Otp).NotEmpty().Length(Common.Otp.Length).Matches("^[0-9]+$");
            RuleFor(r => r)
                .Must(r => r.DeviceId.HasValue || r.Device is not null)
                .WithMessage("Provide deviceId (known device) and/or device info (new device).");
            RuleFor(r => r.Device!).SetValidator(new DeviceDetailsValidator())
                .When(r => r.Device is not null);
        }
    }

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/auth/change-device", Handle)
                .WithSummary("Verify the device-change OTP, register the new device and move the binding to it")
                .WithTags("Auth");
    }

    private static async Task<IResult> Handle(
        Request request,
        AppDbContext db,
        IValidator<Request> validator,
        CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ApiResults.ValidationFailed(validation);

        var email = request.Username.Trim().ToLowerInvariant();
        var profile = await db.Profiles.FirstOrDefaultAsync(p => p.EmailAddress == email, ct);
        if (profile is null)
            return ApiResults.Fail(StatusCodes.Status422UnprocessableEntity,
                "No profile exists for this account. Use the resume flow instead.");

        var (_, error) = await OtpFlow.CheckAsync(
            db, request.RetrievalCode, OtpPurpose.DeviceChange, profile.Id,
            profile.EmailAddress, request.Otp,
            "POST /api/auth/initiate-device-change", ct);
        if (error is not null)
            return error;

        // Register or resolve the new device — this OTP is its approval.
        var device = await DeviceResolver.ResolveAsync(db, request.DeviceId, request.Device, ct);
        if (device is null)
            return ApiResults.Fail(StatusCodes.Status422UnprocessableEntity,
                $"No device registered with id '{request.DeviceId}'. Send full device info to register it.");

        // Exclusive rebind: releases the profile's previous device and, if this device
        // was mapped to another profile, releases it there too (history rows are kept).
        var binding = await UserDevices.BindAsync(db, profile.Id, device.Id, ct);

        var now = DateTime.UtcNow;
        profile.DeviceChangedAtUtc = now;
        profile.UpdatedAtUtc = now;
        ActivityLog.Record(db, ActivityType.DeviceChanged, profile.Id, device.Id,
            $"Active device changed to '{device.Name}' (credential + OTP approved).");
        await db.SaveChangesAsync(ct);

        return ApiResults.Ok(new Response(device.Id, now, DeviceRecentlyLinked: true),
            "Device changed successfully. You can now log in from this device.");
    }
}
