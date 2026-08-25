using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Common.MessageCentre;
using ProfileSvr.Common.Sso;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Features.Auth;

/// <summary>
/// Device-change for a locked-out user: they cannot log in from the new device (403) and
/// hold no token or profile id — so this flow authenticates with the account credentials,
/// resolves the profile from them, and sends the approval OTP to the account email.
/// </summary>
public static class InitiateDeviceChange
{
    public record Request(string Username, string Password);

    private record Response(string Channel, string RetrievalCode, DateTime ExpiresAtUtc);

    public class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(r => r.Username).NotEmpty().MaximumLength(320);
            RuleFor(r => r.Password).NotEmpty().MaximumLength(128);
        }
    }

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/auth/initiate-device-change", Handle)
                .WithSummary("Verify account credentials and send an email OTP approving a device change (no token needed)")
                .WithTags("Auth");
    }

    private static async Task<IResult> Handle(
        Request request,
        AppDbContext db,
        ISsoClient sso,
        IMessageCentre messageCentre,
        IValidator<Request> validator,
        CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ApiResults.ValidationFailed(validation);

        // The password is the first factor — proves account ownership without a device.
        try
        {
            await sso.PasswordLoginAsync(request.Username.Trim(), request.Password, ct);
        }
        catch (SsoException ex)
        {
            return ApiResults.Fail(StatusCodes.Status401Unauthorized, ex.Message);
        }
        catch (HttpRequestException)
        {
            return ApiResults.Fail(StatusCodes.Status503ServiceUnavailable,
                "Could not reach the SSO service. Try again later.");
        }

        var email = request.Username.Trim().ToLowerInvariant();
        var profile = await db.Profiles.FirstOrDefaultAsync(p => p.EmailAddress == email, ct);
        if (profile is null)
            return ApiResults.Fail(StatusCodes.Status422UnprocessableEntity,
                "No profile exists for this account. Use the resume flow instead.");

        var (issued, error) = await OtpFlow.IssueEmailAsync(db, profile, OtpPurpose.DeviceChange, null, ct);
        if (error is not null)
            return error;

        try
        {
            await messageCentre.SendEmailOtpAsync(
                profile.EmailAddress, issued!.PlainCode,
                heading: "Approve your device change",
                intro: "A request was made to link your account to a new device. Enter the code below to approve it. If this wasn't you, ignore this email and consider changing your password.",
                ct);
        }
        catch (HttpRequestException ex)
        {
            issued!.Otp.ConsumedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return ApiResults.Fail(StatusCodes.Status502BadGateway,
                $"Could not send the OTP email. {ex.Message}");
        }

        return ApiResults.Accepted(new Response("email", issued!.Otp.RetrievalCode, issued.Otp.ExpiresAtUtc),
            "OTP sent to your email address.");
    }
}
