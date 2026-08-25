using System.Security.Claims;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Features.Profiles;

public static class SetTransactionPin
{
    public record Request(Guid DeviceId, string Pin);

    private record Response(Guid ProfileId, bool HasSetTransactionPin, DateTime? UpdatedAtUtc);

    public class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(r => r.DeviceId).NotEmpty();
            RuleFor(r => r.Pin)
                .NotEmpty()
                .Matches(@"^\d{4}$")
                .WithMessage("The transaction PIN must be exactly 4 digits.");
        }
    }

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/profiles/set-pin", Handle)
                .RequireAuthorization(TokenTypes.ProfileActivePolicy)
                .WithSummary("Set or change the caller's transaction PIN (profile from token; device must be the active one). Verification happens during transactions.")
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

        if (profile.HasSetTransactionPin)
            return ApiResults.Conflict(
                "A transaction PIN is already set. Use POST /api/profiles/pin-change or the pin-reset flow.");

        profile.TransactionPinSalt = TransactionPin.GenerateSalt();
        profile.TransactionPinHash = TransactionPin.Hash(request.Pin, profile.TransactionPinSalt);
        profile.HasSetTransactionPin = true;
        profile.UpdatedAtUtc = DateTime.UtcNow;
        ActivityLog.Record(db, ActivityType.PinSet, profile.Id, request.DeviceId, "Transaction PIN set.");
        await db.SaveChangesAsync(ct);

        return ApiResults.Ok(new Response(profile.Id, true, profile.UpdatedAtUtc),
            "Transaction PIN set.");
    }
}
