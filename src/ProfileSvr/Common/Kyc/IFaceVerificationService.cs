namespace ProfileSvr.Common.Kyc;

/// <summary>A face-liveness session the client SDK runs against AWS Rekognition.</summary>
public record LivenessSession(string SessionId, string AuthToken);

/// <summary>Outcome of a completed liveness session.</summary>
public record LivenessResult(string SessionId, double Confidence, bool IsLive, string? ReferenceImageBase64);

/// <summary>Outcome of comparing the identity photo with the liveness selfie.</summary>
public record FaceComparison(bool IsMatch, double Confidence);

/// <summary>
/// Face liveness + comparison used to complete KYC. AWS Rekognition in production,
/// a permissive mock for local dev.
/// </summary>
public interface IFaceVerificationService
{
    /// <summary>Opens a liveness session and packs temporary credentials for the client SDK.</summary>
    Task<LivenessSession> CreateLivenessSessionAsync(CancellationToken ct);

    /// <summary>Fetches the result (confidence + reference selfie) of a finished liveness session.</summary>
    Task<LivenessResult> GetLivenessResultAsync(string sessionId, CancellationToken ct);

    /// <summary>Compares the identity photo (source) with the liveness selfie (target).</summary>
    Task<FaceComparison> CompareFacesAsync(string sourceImageBase64, string targetImageBase64, CancellationToken ct);
}

public class FaceVerificationException(string message, Exception? inner = null) : Exception(message, inner);
