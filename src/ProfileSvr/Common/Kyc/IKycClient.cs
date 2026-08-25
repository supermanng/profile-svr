namespace ProfileSvr.Common.Kyc;

/// <summary>Identity details returned by the KYC provider for a BVN or NIN.</summary>
public record KycDetails(
    string FirstName,
    string LastName,
    string? MiddleName,
    DateOnly? DateOfBirth,
    string? Gender,
    string PhoneNumber,
    string? Address);

public interface IKycClient
{
    /// <summary>Looks up a BVN. Returns null when the BVN is not found.</summary>
    Task<KycDetails?> LookupBvnAsync(string bvn, CancellationToken ct);

    /// <summary>Looks up a NIN. Returns null when the NIN is not found.</summary>
    Task<KycDetails?> LookupNinAsync(string nin, CancellationToken ct);
}

public class KycException(string message) : Exception(message);
