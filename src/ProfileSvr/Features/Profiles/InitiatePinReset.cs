using System.Security.Claims;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Common.MessageCentre;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Features.Profiles;

public static class InitiatePinReset
{
    public record Request(Guid DeviceId);

    private record Response(Guid ProfileId, string Channel, string RetrievalCode, DateTime ExpiresAtUtc);

    public class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(r => r.DeviceId).NotEmpty();
        }
    }

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/profiles/initiate-pin-reset", Handle)
                .RequireAuthorization(TokenTypes.ProfileActivePolicy)
                .WithSummary("Send an email OTP to approve resetting the caller's transaction PIN")
                .WithTags("Profiles");
    }

    private static async Task<IResult> Handle(
        Request request,
        ClaimsPrincipal user,
        AppDbContext db,
        IMessageCentre messageCentre,
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

        var (issued, error) = await OtpFlow.IssueEmailAsync(db, profile, OtpPurpose.PinReset, request.DeviceId, ct);
        if (error is not null)
            return error;

        try
        {
            await messageCentre.SendEmailOtpAsync(
                profile.EmailAddress, issued!.PlainCode,
                heading: "Reset your transaction PIN",
                intro: "A request was made to reset your transaction PIN. Enter the code below to approve it. If this wasn't you, ignore this email and consider changing your password.",
                ct);
        }
        catch (HttpRequestException ex)
        {
            issued!.Otp.ConsumedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return ApiResults.Fail(StatusCodes.Status502BadGateway,
                $"Could not send the OTP email. {ex.Message}");
        }

        return ApiResults.Accepted(new Response(
            profile.Id, "email", issued!.Otp.RetrievalCode, issued.Otp.ExpiresAtUtc),
            "OTP sent to your email address.");
    }
}
