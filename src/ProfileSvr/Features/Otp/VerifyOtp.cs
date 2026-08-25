using System.Security.Claims;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Features.OtpVerification;

/// <summary>
/// Single OTP verification endpoint — the section being verified is passed as an enum:
///   Email → confirms the email address,
///   Phone → confirms the phone number,
///   Kyc   → confirms the phone from the BVN/NIN record, marks the identity verified, tier 1.
/// Flows with extra inputs/side effects (onboarding verify, pin-reset, password-reset,
/// change-device) keep their own endpoints.
/// </summary>
public static class VerifyOtp
{
    public enum VerificationSection
    {
        Email,
        Phone,
        Kyc
    }

    public record Request(Guid DeviceId, string RetrievalCode, string Otp, VerificationSection Section);

    private record Response(
        Guid ProfileId,
        string Section,
        bool EmailConfirmed,
        bool PhoneNumberConfirmed,
        bool BvnIsVerified,
        bool NinIsVerified,
        int Tier,
        DateTime? UpdatedAtUtc);

    public class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(r => r.DeviceId).NotEmpty();
            RuleFor(r => r.RetrievalCode).NotEmpty().MaximumLength(32);
            RuleFor(r => r.Otp).NotEmpty().Length(Common.Otp.Length).Matches("^[0-9]+$");
            RuleFor(r => r.Section).IsInEnum();
        }
    }

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/otp/verify", Handle)
                .RequireAuthorization()
                .WithSummary("Verify an OTP for the given section (Email | Phone | Kyc)")
                .WithTags("Otp");
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

        // Section → OTP purpose and the value the code hash is bound to.
        var purpose = request.Section switch
        {
            VerificationSection.Email => OtpPurpose.EmailConfirmation,
            VerificationSection.Phone => OtpPurpose.PhoneConfirmation,
            _ => OtpPurpose.KycVerification
        };

        var boundTo = request.Section == VerificationSection.Email
            ? profile.EmailAddress
            : profile.PhoneNumber;
        if (string.IsNullOrWhiteSpace(boundTo))
            return ApiResults.Fail(StatusCodes.Status422UnprocessableEntity,
                "The profile has no phone number for this verification. Initiate the flow first.");

        // Idempotent success when the section is already verified.
        if (request.Section == VerificationSection.Email && profile.EmailConfirmed)
            return Ok(profile, request.Section);
        if (request.Section == VerificationSection.Phone && profile.PhoneNumberConfirmed)
            return Ok(profile, request.Section);

        var (_, error) = await OtpFlow.CheckAsync(
            db, request.RetrievalCode, purpose, profile.Id, boundTo, request.Otp,
            "the matching initiate endpoint", ct);
        if (error is not null)
            return error;

        var now = DateTime.UtcNow;
        switch (request.Section)
        {
            case VerificationSection.Email:
                profile.EmailConfirmed = true;
                ActivityLog.Record(db, ActivityType.EmailVerified, profile.Id, request.DeviceId,
                    $"Email {profile.EmailAddress} verified.");
                break;

            case VerificationSection.Phone:
                profile.PhoneNumberConfirmed = true;
                ActivityLog.Record(db, ActivityType.PhoneVerified, profile.Id, request.DeviceId,
                    $"Phone number {profile.PhoneNumber} verified.");
                break;

            case VerificationSection.Kyc:
                profile.PhoneNumberConfirmed = true;
                string verifiedId;
                if (profile.Bvn is not null && !profile.BvnIsVerified)
                {
                    profile.BvnIsVerified = true;
                    verifiedId = "BVN";
                }
                else if (profile.Nin is not null && !profile.NinIsVerified)
                {
                    profile.NinIsVerified = true;
                    verifiedId = "NIN";
                }
                else
                {
                    verifiedId = "identity";
                }
                if (profile.Tier < 1)
                    profile.Tier = 1;
                ActivityLog.Record(db, ActivityType.PhoneVerified, profile.Id, request.DeviceId,
                    $"Phone number {profile.PhoneNumber} verified via KYC.");
                ActivityLog.Record(db, ActivityType.KycVerified, profile.Id, request.DeviceId,
                    $"{verifiedId} verified; profile moved to tier {profile.Tier}.");
                break;
        }

        profile.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);

        return Ok(profile, request.Section);
    }

    private static IResult Ok(Profile profile, VerificationSection section) =>
        ApiResults.Ok(new Response(
            profile.Id, section.ToString(), profile.EmailConfirmed, profile.PhoneNumberConfirmed,
            profile.BvnIsVerified, profile.NinIsVerified, profile.Tier, profile.UpdatedAtUtc),
            section switch
            {
                VerificationSection.Email => "Email address verified.",
                VerificationSection.Phone => "Phone number verified.",
                _ => $"Identity verified. Profile upgraded to tier {profile.Tier}."
            });
}
