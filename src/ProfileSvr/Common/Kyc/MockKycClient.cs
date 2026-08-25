namespace ProfileSvr.Common.Kyc;

/// <summary>
/// Dev fallback used when Kyc:BaseUrl is not configured — returns deterministic
/// fake identity data derived from the submitted number. Never use in production.
/// </summary>
public class MockKycClient(ILogger<MockKycClient> logger) : IKycClient
{
    public Task<KycDetails?> LookupBvnAsync(string bvn, CancellationToken ct) => LookupAsync(bvn, "BVN");

    public Task<KycDetails?> LookupNinAsync(string nin, CancellationToken ct) => LookupAsync(nin, "NIN");

    private Task<KycDetails?> LookupAsync(string number, string kind)
    {
        // Numbers ending in 00 simulate "not found".
        if (number.EndsWith("00"))
        {
            logger.LogWarning("[DEV ONLY] Mock KYC: {Kind} {Number} not found", kind, number);
            return Task.FromResult<KycDetails?>(null);
        }

        logger.LogWarning("[DEV ONLY] Mock KYC lookup for {Kind} {Number}", kind, number);
        return Task.FromResult<KycDetails?>(new KycDetails(
            FirstName: "Adaeze",
            LastName: "Okafor",
            MiddleName: null,
            DateOfBirth: new DateOnly(1995, 5, 15),
            Gender: "Female",
            PhoneNumber: "+234801" + number[^7..],
            Address: "12 Marina Road, Lagos Island, Lagos"));
    }
}
