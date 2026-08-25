using System.Net;
using System.Net.Http.Json;

namespace ProfileSvr.Common.Kyc;

/// <summary>
/// HTTP client for the basic-KYC service.
/// Assumed contract (adjust here if the provider differs — this is the only file that knows it):
///   GET {BaseUrl}/api/kyc/bvn/{bvn}   and   GET {BaseUrl}/api/kyc/nin/{nin}
/// returning { isSuccess, data: { firstName, lastName, middleName, dateOfBirth,
///             gender, phoneNumber, address }, message } — 404/isSuccess:false = not found.
/// Configure via Kyc:BaseUrl and optional Kyc:ApiKey (sent as X-Api-Key).
/// </summary>
public class KycHttpClient(HttpClient http) : IKycClient
{
    private record KycData(
        string? FirstName, string? LastName, string? MiddleName, DateOnly? DateOfBirth,
        string? Gender, string? PhoneNumber, string? Address);

    private record KycResponse(bool IsSuccess, KycData? Data, string? Message);

    public Task<KycDetails?> LookupBvnAsync(string bvn, CancellationToken ct) =>
        LookupAsync($"/api/kyc/bvn/{Uri.EscapeDataString(bvn)}", "BVN", ct);

    public Task<KycDetails?> LookupNinAsync(string nin, CancellationToken ct) =>
        LookupAsync($"/api/kyc/nin/{Uri.EscapeDataString(nin)}", "NIN", ct);

    private async Task<KycDetails?> LookupAsync(string path, string kind, CancellationToken ct)
    {
        var response = await http.GetAsync(path, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        if (!response.IsSuccessStatusCode)
            throw new KycException($"The KYC service returned {(int)response.StatusCode} for the {kind} lookup.");

        var body = await response.Content.ReadFromJsonAsync<KycResponse>(ct);
        if (body is not { IsSuccess: true, Data: not null })
            return null;

        var d = body.Data;
        if (string.IsNullOrWhiteSpace(d.FirstName) ||
            string.IsNullOrWhiteSpace(d.LastName) ||
            string.IsNullOrWhiteSpace(d.PhoneNumber))
            throw new KycException($"The KYC service returned an incomplete {kind} record.");

        return new KycDetails(
            d.FirstName.Trim(), d.LastName.Trim(), d.MiddleName?.Trim(),
            d.DateOfBirth, d.Gender?.Trim(), d.PhoneNumber.Trim(), d.Address?.Trim());
    }
}
