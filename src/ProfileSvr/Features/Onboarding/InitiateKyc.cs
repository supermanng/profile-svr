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
/// KYC onboarding path: after the email is verified (profile exists, user logged in),
/// the user submits their BVN or NIN. The profile details are loaded from the KYC record,
/// the phone number comes from the BVN/NIN — never from the client — and the OTP is sent
/// to that phone over WhatsApp. Verify with POST /api/otp/verify (section Kyc).
/// </summary>
public static class InitiateKyc
{
    public record Request(Guid DeviceId, string? Bvn, string? Nin);

    private record Response(
        Guid ProfileId,
        string IdType,
        string MaskedPhoneNumber,
        string Channel,
        string RetrievalCode,
        DateTime ExpiresAtUtc);

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
                .RequireAuthorization(TokenTypes.OnboardingPolicy)
                .WithSummary("Load profile details from BVN/NIN and send an OTP to the phone number on the KYC record")
                .WithTags("Onboarding");
    }

    private static async Task<IResult> Handle(
        Request request,
        ClaimsPrincipal user,
        AppDbContext db,
        IKycClient kyc,
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

        var phone = details.PhoneNumber;
        var phoneTaken = await db.Profiles.AnyAsync(p => p.Id != profile.Id && p.PhoneNumber == phone, ct);
        if (phoneTaken)
            return ApiResults.Conflict("The phone number on this identity record is already used by another profile.");

        // Load the profile from the KYC record; the phone comes from the identity, unconfirmed
        // until the OTP sent to it is verified.
        profile.FirstName = details.FirstName;
        profile.LastName = details.LastName;
        profile.MiddleName = details.MiddleName;
        profile.DateOfBirth = details.DateOfBirth ?? profile.DateOfBirth;
        profile.Gender = details.Gender;
        profile.Address = details.Address;
        profile.PhoneNumber = phone;
        profile.PhoneNumberConfirmed = false;
        if (usingBvn) { profile.Bvn = idNumber; profile.BvnIsVerified = false; }
        else { profile.Nin = idNumber; profile.NinIsVerified = false; }
        profile.UpdatedAtUtc = DateTime.UtcNow;

        var (issued, error) = await OtpFlow.IssueEmailAsync(db, profile, OtpPurpose.KycVerification, request.DeviceId, ct);
        if (error is not null)
            return error;

        // The OTP targets the phone from the KYC record, not the email.
        issued!.Otp.Channel = messageCentre.PhoneOtpChannel;
        issued.Otp.Target = phone;
        issued.Otp.CodeHash = Otp.Hash(issued.PlainCode, OtpPurpose.KycVerification, phone);
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
                $"Could not send the OTP via WhatsApp. {ex.Message}");
        }

        return ApiResults.Accepted(new Response(
            profile.Id, idType, Mask(phone), "whatsapp", issued.Otp.RetrievalCode, issued.Otp.ExpiresAtUtc),
            $"OTP sent to the phone number on your {idType.ToUpperInvariant()} record.");
    }

    private static string Mask(string phone) =>
        phone.Length <= 4 ? phone : new string('*', phone.Length - 4) + phone[^4..];
}
