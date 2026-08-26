using Refit;

namespace ProfileSvr.Common.Accounts;

/// <summary>A virtual (collection) account created at the provider.</summary>
public record VirtualAccountInfo(string AccountNumber, string? AccountName, string? BankName);

/// <summary>
/// The replaceable virtual-account (collection) integration. VantPay today — implement this
/// for another collections provider and swap the registration in Program.cs.
/// </summary>
public interface IVirtualAccountProvider
{
    /// <summary>Creates a static virtual account for the identity; null when the provider refuses.</summary>
    Task<VirtualAccountInfo?> CreateVirtualAccountAsync(string? bvn, string? nin, CancellationToken ct);
}

public record CreateVirtualAccountRequest(
    string? Bvn, string? Nin, string? Prefix, string? TerminalId,
    decimal Amount, bool IsPos, bool PrefixRequired);

public record VirtualAccountData(string? AccountNumber, string? AccountName, string? Bank);

public record VirtualAccountEnvelope(bool IsSuccessful, string? Message, string? Code, VirtualAccountData? Data);

/// <summary>
/// VantPay (DigitVirtual) merchant API surface — the same endpoint vliquidity uses.
/// Auth: <see cref="VirtualAccountAuthHandler"/> attaches a VantPay-SSO client-credentials token.
/// </summary>
public interface IVirtualAccountApi
{
    [Post("/api/v1/virtual-accounts/create/static")]
    Task<IApiResponse<VirtualAccountEnvelope>> CreateVirtualAccountAsync(
        [Body] CreateVirtualAccountRequest request, CancellationToken ct);
}

/// <summary>
/// VantPay implementation over the Refit <see cref="IVirtualAccountApi"/> client.
/// Config: DigitVirtual:{BaseUrl, SsoBaseUrl, ClientId, ClientSecret, BankName}.
/// </summary>
public class VantPayVirtualAccountProvider(IVirtualAccountApi api, IConfiguration configuration)
    : IVirtualAccountProvider
{
    public async Task<VirtualAccountInfo?> CreateVirtualAccountAsync(string? bvn, string? nin, CancellationToken ct)
    {
        using var response = await api.CreateVirtualAccountAsync(new CreateVirtualAccountRequest(
            Bvn: bvn,
            Nin: nin,
            Prefix: string.Empty,
            TerminalId: string.Empty,
            Amount: 0,
            IsPos: false,
            PrefixRequired: false), ct);

        if (!response.IsSuccessStatusCode)
        {
            // Surface the provider's error body — it carries the actual rejection reason.
            var detail = response.Error?.Content;
            throw new AccountProviderException(
                $"The virtual-account service returned {(int)response.StatusCode}." +
                (string.IsNullOrWhiteSpace(detail)
                    ? string.Empty
                    : $" Response: {(detail.Length > 500 ? detail[..500] + "…" : detail)}"));
        }

        var data = response.Content?.Data;
        if (response.Content?.IsSuccessful != true || string.IsNullOrWhiteSpace(data?.AccountNumber))
            return null;

        return new VirtualAccountInfo(
            data.AccountNumber,
            data.AccountName,
            // The provider's bank name wins; fall back to the configured one.
            string.IsNullOrWhiteSpace(data.Bank) ? configuration["DigitVirtual:BankName"] : data.Bank);
    }
}

/// <summary>
/// Dev fallback used when DigitVirtual:BaseUrl is not configured — deterministic virtual
/// accounts. Never use in production.
/// </summary>
public class MockVirtualAccountProvider(ILogger<MockVirtualAccountProvider> logger) : IVirtualAccountProvider
{
    public Task<VirtualAccountInfo?> CreateVirtualAccountAsync(string? bvn, string? nin, CancellationToken ct)
    {
        var seed = bvn ?? nin ?? "unknown";
        var accountNumber = "88" + seed[^Math.Min(8, seed.Length)..].PadLeft(8, '0');
        logger.LogWarning("[DEV ONLY] Mock virtual account {AccountNumber}", accountNumber);
        return Task.FromResult<VirtualAccountInfo?>(
            new VirtualAccountInfo(accountNumber, "Mock Virtual Account", "Mock Microfinance Bank"));
    }
}
