using System.Text;
using Amazon;
using Amazon.Rekognition;
using Amazon.Rekognition.Model;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;

namespace ProfileSvr.Common.Kyc;

/// <summary>
/// AWS Rekognition implementation (same mechanics as vliquidity):
/// - liveness sessions via CreateFaceLivenessSession; the client SDK receives temporary STS
///   credentials packed as base64("{sessionToken}##{reversed secret}##{reversed key id}")
/// - a session is "live" at confidence >= 75; faces match at similarity >= 80.
/// Configure via Aws:AccessKey, Aws:SecretKey, Aws:Region.
/// </summary>
public class AwsFaceVerificationService : IFaceVerificationService
{
    public const double LivenessConfidenceThreshold = 75.0;
    public const float FaceSimilarityThreshold = 80f;

    private readonly IAmazonRekognition _rekognition;
    private readonly IAmazonSecurityTokenService _sts;
    private readonly ILogger<AwsFaceVerificationService> _logger;

    public AwsFaceVerificationService(IConfiguration configuration, ILogger<AwsFaceVerificationService> logger)
    {
        _logger = logger;

        var accessKey = configuration["Aws:AccessKey"];
        var secretKey = configuration["Aws:SecretKey"];
        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
            throw new InvalidOperationException("Aws:AccessKey / Aws:SecretKey are not configured.");

        var credentials = new BasicAWSCredentials(accessKey, secretKey);
        var region = RegionEndpoint.GetBySystemName(configuration["Aws:Region"] ?? "us-east-1");
        _rekognition = new AmazonRekognitionClient(credentials, region);
        _sts = new AmazonSecurityTokenServiceClient(credentials, region);
    }

    public async Task<LivenessSession> CreateLivenessSessionAsync(CancellationToken ct)
    {
        try
        {
            var session = await _rekognition.CreateFaceLivenessSessionAsync(
                new CreateFaceLivenessSessionRequest(), ct);
            var sts = await _sts.GetSessionTokenAsync(
                new GetSessionTokenRequest { DurationSeconds = 3600 }, ct);
            return new LivenessSession(session.SessionId, PackAuthToken(sts.Credentials));
        }
        catch (AmazonServiceException ex)
        {
            _logger.LogError(ex, "AWS error creating liveness session. ErrorCode: {ErrorCode}", ex.ErrorCode);
            throw new FaceVerificationException("Failed to create the liveness session.", ex);
        }
    }

    // The client SDK unpacks this blob to obtain its temporary AWS credentials.
    private static string PackAuthToken(Credentials credentials)
    {
        var reversedKeyId = new string(credentials.AccessKeyId.Reverse().ToArray());
        var reversedSecret = new string(credentials.SecretAccessKey.Reverse().ToArray());
        return Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{credentials.SessionToken}##{reversedSecret}##{reversedKeyId}"));
    }

    public async Task<LivenessResult> GetLivenessResultAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            var result = await _rekognition.GetFaceLivenessSessionResultsAsync(
                new GetFaceLivenessSessionResultsRequest { SessionId = sessionId }, ct);

            var confidence = (double)result.Confidence;
            return new LivenessResult(
                sessionId,
                confidence,
                confidence >= LivenessConfidenceThreshold,
                result.ReferenceImage?.Bytes is { } bytes ? Convert.ToBase64String(bytes.ToArray()) : null);
        }
        catch (AmazonServiceException ex)
        {
            _logger.LogError(ex, "AWS error fetching liveness results for {SessionId}. ErrorCode: {ErrorCode}",
                sessionId, ex.ErrorCode);
            throw new FaceVerificationException("Failed to get the liveness session results.", ex);
        }
    }

    public async Task<FaceComparison> CompareFacesAsync(
        string sourceImageBase64, string targetImageBase64, CancellationToken ct)
    {
        try
        {
            var response = await _rekognition.CompareFacesAsync(new CompareFacesRequest
            {
                SourceImage = new Image { Bytes = new MemoryStream(Convert.FromBase64String(sourceImageBase64)) },
                TargetImage = new Image { Bytes = new MemoryStream(Convert.FromBase64String(targetImageBase64)) },
                SimilarityThreshold = FaceSimilarityThreshold
            }, ct);

            var best = response.FaceMatches?.OrderByDescending(m => m.Similarity).FirstOrDefault();
            if (best is null)
                return new FaceComparison(false, 0.0);

            var confidence = (double)best.Similarity;
            return new FaceComparison(confidence >= FaceSimilarityThreshold, confidence);
        }
        catch (FormatException)
        {
            throw new FaceVerificationException("One of the images is not valid base64.");
        }
        catch (AmazonServiceException ex)
        {
            _logger.LogError(ex, "AWS error comparing faces. ErrorCode: {ErrorCode}", ex.ErrorCode);
            throw new FaceVerificationException("Failed to compare the faces.", ex);
        }
    }
}
