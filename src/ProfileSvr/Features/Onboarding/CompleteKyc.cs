using System.Security.Claims;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Common.Accounts;
using ProfileSvr.Common.Kyc;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Features.Onboarding;

/// <summary>
/// Completes the KYC started at POST /api/onboarding/initiate-kyc: fetches the AWS liveness
/// session result, compares the liveness selfie with the BVN/NIN photo, and on success marks
/// the submitted BVN/NIN verified, sets the KYC status (Approved), promotes the profile to
/// tier 1 and then GENERATES THE ACCOUNTS (customer/CIF + NGN + CAD + virtual) — the
/// core-banking provider requires a BVN/NIN, so this post-verification point is where they
/// are created (as in vliquidity). Best-effort: failed steps leave
/// kycStatus: ProvisioningFailed and are retried at create-profile and login/refresh.
/// When the phone still needs confirming (onboarding order), that happens separately via the
/// OTP initiate-kyc sent to it (POST /api/otp/verify, section Phone) — either order works;
/// both are required before create-profile.
/// A failed liveness check or face mismatch is a soft failure: HTTP 200 with isLive/faceMatch
/// false (mirrors vliquidity) — the client inspects the payload and retries with a new session.
/// </summary>
public static class CompleteKyc
{
    public record Request(Guid DeviceId, string SessionId);

    private record Response(
        Guid ProfileId,
        string SessionId,
        string IdType,
        double LivenessScore,
        bool IsLive,
        bool FaceMatch,
        double FaceMatchConfidence,
        bool BvnIsVerified,
        bool NinIsVerified,
        bool PhoneNumberConfirmed,
        int Tier,
        string KycStatus,
        string? Cif,
        string? NairaAccount,
        string? CadAccount,
        string? VirtualAccount,
        string? VirtualAccountBank);

    public class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(r => r.DeviceId).NotEmpty();
            RuleFor(r => r.SessionId).NotEmpty().MaximumLength(128);
        }
    }

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/onboarding/complete-kyc", Handle)
                // Callable during onboarding AND after the profile is active (phone-first journey).
                .RequireAuthorization()
                .WithSummary("Complete KYC: liveness result + face comparison, then verify the BVN/NIN and move to tier 1")
                .WithTags("Onboarding");
    }

    private static async Task<IResult> Handle(
        Request request,
        ClaimsPrincipal user,
        AppDbContext db,
        IKycClient kyc,
        IFaceVerificationService faces,
        IAccountFacade accounts,
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

        // The id staged at initiate-kyc is the one being verified.
        var usingBvn = profile.Bvn is not null && !profile.BvnIsVerified;
        var usingNin = !usingBvn && profile.Nin is not null && !profile.NinIsVerified;
        if (!usingBvn && !usingNin)
            return profile.BvnIsVerified || profile.NinIsVerified
                ? ApiResults.Conflict("This profile's identity is already verified.")
                : ApiResults.Fail(StatusCodes.Status422UnprocessableEntity,
                    "No BVN or NIN is staged for verification. Complete POST /api/onboarding/initiate-kyc first.");
        var idNumber = usingBvn ? profile.Bvn! : profile.Nin!;
        var idType = usingBvn ? "bvn" : "nin";

        // 1. Liveness session result from AWS.
        LivenessResult liveness;
        try
        {
            liveness = await faces.GetLivenessResultAsync(request.SessionId, ct);
        }
        catch (FaceVerificationException)
        {
            return ApiResults.Fail(StatusCodes.Status502BadGateway,
                "Could not retrieve the liveness session result. Try again later.");
        }

        if (!liveness.IsLive)
        {
            profile.KycStatus = KycVerificationStatus.LivenessFailed;
            profile.KycStatusReason = "The liveness check did not pass. Please try again.";
            profile.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return ApiResults.Ok(
                Build(profile, request.SessionId, idType, liveness.Confidence, isLive: false,
                    faceMatch: false, faceMatchConfidence: 0),
                "Liveness check failed.");
        }

        if (string.IsNullOrWhiteSpace(liveness.ReferenceImageBase64))
            return ApiResults.Fail(StatusCodes.Status502BadGateway,
                "The liveness session returned no reference image. Start a new session and try again.");

        // 2. Re-fetch the identity photo for the comparison.
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

        if (string.IsNullOrWhiteSpace(details?.Image))
            return ApiResults.Fail(StatusCodes.Status502BadGateway,
                "Could not retrieve the identity photo for the face comparison. Try again later.");

        // 3. Compare the identity photo (source) with the liveness selfie (target).
        FaceComparison comparison;
        try
        {
            comparison = await faces.CompareFacesAsync(details.Image, liveness.ReferenceImageBase64, ct);
        }
        catch (FaceVerificationException)
        {
            return ApiResults.Fail(StatusCodes.Status502BadGateway,
                "Could not compare the faces. Try again later.");
        }

        if (!comparison.IsMatch)
        {
            profile.KycStatus = KycVerificationStatus.FaceMismatch;
            profile.KycStatusReason =
                $"Your selfie did not match your {idType.ToUpperInvariant()} record. Please retry the verification.";
            profile.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return ApiResults.Ok(
                Build(profile, request.SessionId, idType, liveness.Confidence, isLive: true,
                    faceMatch: false, faceMatchConfidence: comparison.Confidence),
                "Face comparison failed.");
        }

        // 4. Verified: the id is marked verified and the profile moves to tier 1.
        //    (Phone confirmation is the OTP's job — see initiate-kyc.)
        if (usingBvn) profile.BvnIsVerified = true;
        else profile.NinIsVerified = true;
        if (profile.Tier < 1)
            profile.Tier = 1;
        profile.KycStatus = KycVerificationStatus.Approved;
        profile.KycStatusReason = null;
        ActivityLog.Record(db, ActivityType.KycVerified, profile.Id, request.DeviceId,
            $"{idType.ToUpperInvariant()} verified via liveness + face match; profile moved to tier {profile.Tier}.");

        // 5. Generate the accounts (customer/CIF → NGN → CAD → virtual + naira mapping) now
        //    that the BVN/NIN is verified — the provider requires it. Best-effort: failed
        //    steps set KycStatus=ProvisioningFailed and create-profile/login/refresh retry.
        if (await accounts.EnsureAccountsAsync(profile, ct))
            ActivityLog.Record(db, ActivityType.AccountsProvisioned, profile.Id, request.DeviceId,
                $"Banking accounts provisioned (cif: {profile.Cif ?? "-"}, " +
                $"NGN: {profile.NairaAccount ?? "-"}, CAD: {profile.CadAccount ?? "-"}, " +
                $"virtual: {profile.VirtualAccount ?? "-"}).");

        profile.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return ApiResults.Ok(
            Build(profile, request.SessionId, idType, liveness.Confidence, isLive: true,
                faceMatch: true, faceMatchConfidence: comparison.Confidence),
            $"Identity verified. Profile upgraded to tier {profile.Tier}.");
    }

    private static Response Build(
        Profile profile, string sessionId, string idType, double livenessScore,
        bool isLive, bool faceMatch, double faceMatchConfidence) => new(
        profile.Id,
        sessionId,
        idType,
        livenessScore,
        isLive,
        faceMatch,
        faceMatchConfidence,
        profile.BvnIsVerified,
        profile.NinIsVerified,
        profile.PhoneNumberConfirmed,
        profile.Tier,
        profile.KycStatus.ToString(),
        profile.Cif,
        profile.NairaAccount,
        profile.CadAccount,
        profile.VirtualAccount,
        profile.VirtualAccountBank);
}
