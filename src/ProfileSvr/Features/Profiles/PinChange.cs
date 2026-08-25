using System.Security.Claims;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Features.Profiles;

public static class PinChange
{
    public record Request(Guid DeviceId, string CurrentPin, string NewPin);

    private record Response(Guid ProfileId, bool HasSetTransactionPin, DateTime? UpdatedAtUtc);

    public class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(r => r.DeviceId).NotEmpty();
            RuleFor(r => r.CurrentPin).NotEmpty().Matches(@"^\d{4}$");
            RuleFor(r => r.NewPin).NotEmpty().Matches(@"^\d{4}$")
                .WithMessage("The transaction PIN must be exactly 4 digits.");
        }
    }

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/profiles/pin-change", Handle)
                .RequireAuthorization(TokenTypes.ProfileActivePolicy)
                .WithSummary("Change the caller's transaction PIN using the current one")
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

        if (!profile.HasSetTransactionPin)
            return ApiResults.Fail(StatusCodes.Status422UnprocessableEntity,
                "No transaction PIN has been set. Set one via POST /api/profiles/set-pin.");

        if (!TransactionPin.Verify(request.CurrentPin, profile.TransactionPinSalt!, profile.TransactionPinHash!))
            return ApiResults.Fail(StatusCodes.Status400BadRequest, "The current PIN is incorrect.");

        profile.TransactionPinSalt = TransactionPin.GenerateSalt();
        profile.TransactionPinHash = TransactionPin.Hash(request.NewPin, profile.TransactionPinSalt);
        profile.UpdatedAtUtc = DateTime.UtcNow;
        ActivityLog.Record(db, ActivityType.PinChanged, profile.Id, request.DeviceId, "Transaction PIN changed.");
        await db.SaveChangesAsync(ct);

        return ApiResults.Ok(new Response(profile.Id, true, profile.UpdatedAtUtc),
            "Transaction PIN changed.");
    }
}
