using System.Globalization;
using System.Net;
using Refit;

namespace ProfileSvr.Common.Kyc;

public record KycVerifyRequest(string IdNumber, int IdType);

public record KycGatewayRecord(
    string? IdNumber, string? IdType, string? FirstName, string? LastName,
    string? OtherName, string? PhoneNumber, string? Image, string? DateOfBirth,
    // Not part of the documented gateway contract, but captured when present.
    string? Gender);

public record KycGatewayResponse(string? Message, KycGatewayRecord? Data);

/// <summary>
/// Dojah KYC gateway HTTP surface (the same one vliquidity uses).
/// Configure the client via Kyc:BaseUrl and optional Kyc:ApiKey (sent as X-Api-Key).
/// </summary>
public interface IKycApi
{
    // idType: 0 = BVN, 1 = NIN
    [Post("/kyc/verify")]
    Task<IApiResponse<KycGatewayResponse>> VerifyIdAsync([Body] KycVerifyRequest request, CancellationToken ct);
}

/// <summary>
/// <see cref="IKycClient"/> over the Refit <see cref="IKycApi"/> gateway client:
///   POST {BaseUrl}/kyc/verify   body { idNumber, idType }   (idType: 0 = BVN, 1 = NIN)
/// returning { message, data: { idNumber, idType, firstName, lastName, otherName,
///             phoneNumber, image, dateOfBirth } } — data:null (or 404/422) = not found.
/// This is the only file that knows the provider contract.
/// </summary>
public class KycHttpClient(IKycApi api) : IKycClient
{
    public Task<KycDetails?> LookupBvnAsync(string bvn, CancellationToken ct) =>
        LookupAsync(bvn, idType: 0, "BVN", ct);

    public Task<KycDetails?> LookupNinAsync(string nin, CancellationToken ct) =>
        LookupAsync(nin, idType: 1, "NIN", ct);

    private async Task<KycDetails?> LookupAsync(string idNumber, int idType, string kind, CancellationToken ct)
    {
        using var response = await api.VerifyIdAsync(new KycVerifyRequest(idNumber, idType), ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
            return null;

        if (!response.IsSuccessStatusCode)
            throw new KycException($"The KYC service returned {(int)response.StatusCode} for the {kind} lookup.");

        var d = response.Content?.Data;
        if (d is null)
            return null;

        if (string.IsNullOrWhiteSpace(d.FirstName) ||
            string.IsNullOrWhiteSpace(d.LastName) ||
            string.IsNullOrWhiteSpace(d.PhoneNumber))
            throw new KycException($"The KYC service returned an incomplete {kind} record.");

        return new KycDetails(
            d.FirstName.Trim(),
            d.LastName.Trim(),
            string.IsNullOrWhiteSpace(d.OtherName) ? null : d.OtherName.Trim(),
            ParseDate(d.DateOfBirth),
            Gender: string.IsNullOrWhiteSpace(d.Gender) ? null : d.Gender.Trim(),
            d.PhoneNumber.Trim(),
            Address: null,
            string.IsNullOrWhiteSpace(d.Image) ? null : d.Image);
    }

    private static readonly string[] DateFormats =
        ["yyyy-MM-dd", "dd-MM-yyyy", "dd/MM/yyyy", "MM/dd/yyyy", "yyyy/MM/dd"];

    private static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (DateOnly.TryParseExact(value.Trim(), DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var exact))
            return exact;
        if (DateTime.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return DateOnly.FromDateTime(parsed);
        return null;
    }
}
