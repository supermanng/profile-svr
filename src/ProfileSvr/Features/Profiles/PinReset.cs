using System.Security.Claims;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Features.Profiles;

public static class PinReset
{
    public record Request(Guid DeviceId, string RetrievalCode, string Otp, string NewPin);

    private record Response(Guid ProfileId, bool HasSetTransactionPin, DateTime? UpdatedAtUtc);

    public class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(r => r.DeviceId).NotEmpty();
            RuleFor(r => r.RetrievalCode).NotEmpty().MaximumLength(32);
            RuleFor(r => r.Otp).NotEmpty().Length(Common.Otp.Length).Matches("^[0-9]+$");
            RuleFor(r => r.NewPin).NotEmpty().Matches(@"^\d{4}$")
                .WithMessage("The transaction PIN must be exactly 4 digits.");
        }
    }

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/profiles/pin-reset", Handle)
                .RequireAuthorization(TokenTypes.ProfileActivePolicy)
                .WithSummary("Verify the pin-reset OTP and set a new transaction PIN")
                .WithTags("Profiles");
    }

    private static async Task<IResult> Handle(
        Request request,
        ClaimsPrincipal user,
        AppDbContext db,
        IValidator<Request> validator,
        CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ApiResults.ValidationFailed(validation);

        var profile = await ProfileClaims.ResolveProfileAsync(user, db, ct);
        if (profile is null)
            return ApiResults.Fail(StatusCodes.Status403Forbidden,
                "The token does not resolve to a profile on this service.");

        var activeDeviceId = await db.UserDevices
            .Where(ud => ud.ProfileId == profile.Id && ud.ReleasedAtUtc == null)
            .Select(ud => (Guid?)ud.DeviceId)
            .FirstOrDefaultAsync(ct);
        if (activeDeviceId != request.DeviceId)
            return ApiResults.Fail(StatusCodes.Status403Forbidden,
                "This device is not linked to the profile.");

        var (otp, error) = await OtpFlow.CheckAsync(
            db, request.RetrievalCode, OtpPurpose.PinReset, profile.Id,
            profile.EmailAddress, request.Otp,
            "POST /api/profiles/initiate-pin-reset", ct);
        if (error is not null)
            return error;

        profile.TransactionPinSalt = TransactionPin.GenerateSalt();
        profile.TransactionPinHash = TransactionPin.Hash(request.NewPin, profile.TransactionPinSalt);
        profile.HasSetTransactionPin = true;
        profile.UpdatedAtUtc = DateTime.UtcNow;
        ActivityLog.Record(db, ActivityType.PinReset, profile.Id, request.DeviceId, "Transaction PIN reset via OTP.");
        await db.SaveChangesAsync(ct);

        return ApiResults.Ok(new Response(profile.Id, true, profile.UpdatedAtUtc),
            "Transaction PIN reset.");
    }
}
