using System.Security.Claims;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Common.Kyc;
using ProfileSvr.Common.MessageCentre;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Features.Onboarding;

/// <summary>
/// KYC initiation (Dojah lookup + AWS face liveness, as in vliquidity), valid in two orders:
///   • the app's main journey — phone verified, profile created, THEN KYC: the profile keeps
///     its already-confirmed phone, no OTP is sent, and only the identity remains to prove
///     via liveness (complete at POST /api/onboarding/complete-kyc), or
///   • during onboarding (before the phone leg): the phone is taken from the BVN/NIN record —
///     never from the client — and an OTP is sent to it (WhatsApp/SMS per config), verified
///     via POST /api/otp/verify (section Phone).
/// Either way the identity record (names, DOB, photo) is returned so the client can confirm
/// it, the profile is pre-filled from it, and a liveness session is opened for the client SDK.
/// </summary>
public static class InitiateKyc
{
    public record Request(Guid DeviceId, string? Bvn, string? Nin);

    private record IdentityDto(
        string FirstName,
        string LastName,
        string? MiddleName,
        DateOnly? DateOfBirth,
        string? Gender,
        string MaskedPhoneNumber,
        string? Image);

    private record LivenessDto(string SessionId, string AuthToken);

    private record OtpDto(string Channel, string RetrievalCode, DateTime ExpiresAtUtc);

    // Otp is null when the phone was already confirmed (KYC after profile creation).
    private record Response(
        Guid ProfileId,
        string IdType,
        string KycStatus,
        IdentityDto Identity,
        LivenessDto Liveness,
        OtpDto? Otp);

    public class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(r => r.DeviceId).NotEmpty();
            RuleFor(r => r)
                .Must(r => !string.IsNullOrWhiteSpace(r.Bvn) ^ !string.IsNullOrWhiteSpace(r.Nin))
                .WithMessage("Provide either bvn or nin, not both.");
            RuleFor(r => r.Bvn).Matches(@"^\d{11}$").When(r => !string.IsNullOrWhiteSpace(r.Bvn))
                .WithMessage("BVN must be exactly 11 digits.");
            RuleFor(r => r.Nin).Matches(@"^\d{11}$").When(r => !string.IsNullOrWhiteSpace(r.Nin))
                .WithMessage("NIN must be exactly 11 digits.");
        }
    }

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/onboarding/initiate-kyc", Handle)
                // Callable during onboarding AND after the profile is active (phone-first journey).
                .RequireAuthorization()
                .WithSummary("Load identity details from BVN/NIN, open an AWS face-liveness session (and send an OTP to the record's phone when the phone is not yet confirmed)")
                .WithTags("Onboarding");
    }

    private static async Task<IResult> Handle(
        Request request,
        ClaimsPrincipal user,
        AppDbContext db,
        IKycClient kyc,
        IFaceVerificationService faces,
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

        if (!profile.EmailConfirmed)
            return ApiResults.Fail(StatusCodes.Status422UnprocessableEntity,
                "The email address is not confirmed yet.");

        var usingBvn = !string.IsNullOrWhiteSpace(request.Bvn);
        var idNumber = (usingBvn ? request.Bvn : request.Nin)!.Trim();
        var idType = usingBvn ? "bvn" : "nin";

        if (usingBvn && profile.BvnIsVerified)
            return ApiResults.Conflict("A BVN is already verified on this profile.");
        if (!usingBvn && profile.NinIsVerified)
            return ApiResults.Conflict("A NIN is already verified on this profile.");

        // The same BVN/NIN may not be used by two profiles.
        var idTaken = usingBvn
            ? await db.Profiles.AnyAsync(p => p.Id != profile.Id && p.Bvn == idNumber, ct)
            : await db.Profiles.AnyAsync(p => p.Id != profile.Id && p.Nin == idNumber, ct);
        if (idTaken)
            return ApiResults.Conflict(
                $"This {idType.ToUpperInvariant()} is already used by another profile.");

        // Look up the identity on the KYC service.
        KycDetails? details;
        try
        {
            details = usingBvn
                ? await kyc.LookupBvnAsync(idNumber, ct)
                : await kyc.LookupNinAsync(idNumber, ct);
        }
        catch (KycException ex)
        {
            return ApiResults.Fail(StatusCodes.Status502BadGateway, ex.Message);
        }
        catch (HttpRequestException)
        {
            return ApiResults.Fail(StatusCodes.Status503ServiceUnavailable,
                "Could not reach the KYC service. Try again later.");
        }

        if (details is null)
            return ApiResults.Fail(StatusCodes.Status422UnprocessableEntity,
                $"No record found for this {idType.ToUpperInvariant()}.");

        // With the phone already confirmed (KYC after profile creation — the app's main
        // journey), the verified phone is kept and no OTP is needed; during onboarding the
        // phone is adopted from the identity record and proven by an OTP sent to it.
        var phone = details.PhoneNumber;
        var adoptKycPhone = !profile.PhoneNumberConfirmed;
        if (adoptKycPhone)
        {
            var phoneTaken = await db.Profiles.AnyAsync(p => p.Id != profile.Id && p.PhoneNumber == phone, ct);
            if (phoneTaken)
                return ApiResults.Conflict("The phone number on this identity record is already used by another profile.");
        }

        // Open the liveness session before persisting anything, so a failure leaves no state.
        LivenessSession liveness;
        try
        {
            liveness = await faces.CreateLivenessSessionAsync(ct);
        }
        catch (FaceVerificationException)
        {
            return ApiResults.Fail(StatusCodes.Status503ServiceUnavailable,
                "Could not start the liveness check. Try again later.");
        }

        // Load the profile from the KYC record.
        profile.FirstName = details.FirstName;
        profile.LastName = details.LastName;
        profile.MiddleName = details.MiddleName;
        profile.DateOfBirth = details.DateOfBirth ?? profile.DateOfBirth;
        profile.Gender = details.Gender ?? profile.Gender;
        profile.Address = details.Address ?? profile.Address;
        if (adoptKycPhone)
        {
            profile.PhoneNumber = phone;
            profile.PhoneNumberConfirmed = false;
        }
        if (usingBvn) { profile.Bvn = idNumber; profile.BvnIsVerified = false; }
        else { profile.Nin = idNumber; profile.NinIsVerified = false; }
        profile.KycStatus = KycVerificationStatus.Pending;
        profile.KycStatusReason = null;
        profile.UpdatedAtUtc = DateTime.UtcNow;

        OtpDto? otpInfo = null;
        if (adoptKycPhone)
        {
            // The OTP targets the phone from the KYC record, not the email; verifying it
            // (section Phone) is what confirms the phone. Standard cooldown/supersede rules.
            var (issued, error) = await OtpFlow.IssueEmailAsync(
                db, profile, OtpPurpose.PhoneConfirmation, request.DeviceId, ct);
            if (error is not null)
                return error;

            issued!.Otp.Channel = messageCentre.PhoneOtpChannel;
            issued.Otp.Target = phone;
            issued.Otp.CodeHash = Otp.Hash(issued.PlainCode, OtpPurpose.PhoneConfirmation, phone);
            await db.SaveChangesAsync(ct);

            try
            {
                await messageCentre.SendPhoneOtpAsync(phone, issued.PlainCode, ct);
            }
            catch (HttpRequestException ex)
            {
                issued.Otp.ConsumedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return ApiResults.Fail(StatusCodes.Status502BadGateway,
                    $"Could not send the OTP to the phone on the {idType.ToUpperInvariant()} record. {ex.Message}");
            }

            otpInfo = new OtpDto(issued.Otp.Channel.ToString(), issued.Otp.RetrievalCode, issued.Otp.ExpiresAtUtc);
        }
        else
        {
            await db.SaveChangesAsync(ct);
        }

        return ApiResults.Ok(new Response(
                profile.Id,
                idType,
                profile.KycStatus.ToString(),
                new IdentityDto(
                    details.FirstName, details.LastName, details.MiddleName,
                    details.DateOfBirth, details.Gender, Mask(phone), details.Image),
                new LivenessDto(liveness.SessionId, liveness.AuthToken),
                otpInfo),
            otpInfo is null
                ? $"Confirm the {idType.ToUpperInvariant()} details and complete the liveness check."
                : $"Confirm the {idType.ToUpperInvariant()} details, verify the OTP sent to the phone on the record, " +
                  "and complete the liveness check.");
    }

    private static string Mask(string phone) =>
        phone.Length <= 4 ? phone : new string('*', phone.Length - 4) + phone[^4..];
}
