namespace ProfileSvr.Common.Kyc;

/// <summary>
/// Dev fallback used when Aws:AccessKey is not configured — every liveness session passes
/// with high confidence and every face comparison matches. Never use in production.
/// </summary>
public class MockFaceVerificationService(ILogger<MockFaceVerificationService> logger) : IFaceVerificationService
{
    public Task<LivenessSession> CreateLivenessSessionAsync(CancellationToken ct)
    {
        var sessionId = Guid.NewGuid().ToString();
        logger.LogWarning("[DEV ONLY] Mock liveness session {SessionId} created", sessionId);
        return Task.FromResult(new LivenessSession(sessionId, "mock-auth-token"));
    }

    public Task<LivenessResult> GetLivenessResultAsync(string sessionId, CancellationToken ct)
    {
        logger.LogWarning("[DEV ONLY] Mock liveness result for session {SessionId}", sessionId);
        return Task.FromResult(new LivenessResult(sessionId, 99.9, true, MockKycClient.TinyPngBase64));
    }

    public Task<FaceComparison> CompareFacesAsync(
        string sourceImageBase64, string targetImageBase64, CancellationToken ct)
    {
        logger.LogWarning("[DEV ONLY] Mock face comparison — always a match");
        return Task.FromResult(new FaceComparison(true, 99.5));
    }
}
