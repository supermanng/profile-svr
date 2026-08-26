namespace ProfileSvr.Common.Accounts;

/// <summary>Currencies provisioned for every KYC-verified profile.</summary>
public static class AccountCurrencies
{
    public const string Naira = "NGN";
    public const string CanadianDollar = "CAD";
}

/// <summary>Customer data sent to the core-banking provider when opening the banking customer (CIF).</summary>
public record AccountHolder(
    string FirstName,
    string LastName,
    string? MiddleName,
    string? Gender,
    DateOnly? DateOfBirth,
    string Email,
    string? PhoneNumber,
    string? Bvn,
    string? Nin);

/// <summary>A deposit account held by the customer at the provider, with its live balance.</summary>
public record AccountDetail(
    string? AccountName,
    string? AccountNumber,
    string? Currency,
    decimal Balance);

/// <summary>An exchange rate published by the provider.</summary>
public record CurrencyRate(
    string SourceCode,
    string TargetCode,
    string? Type,
    decimal BuyRate,
    decimal SellRate,
    DateTime? Date,
    DateTime? UpdatedAtUtc);

/// <summary>
/// The replaceable core-banking integration behind <see cref="IAccountFacade"/>.
/// This is the plug-and-play seam: implement it for another bank (today's implementation
/// is OneCore) and swap the registration in Program.cs — no endpoint changes needed.
/// </summary>
public interface IAccountProvider
{
    /// <summary>Finds or creates the banking customer; returns its id (CIF), or null when refused.</summary>
    Task<string?> GetOrCreateCustomerAsync(AccountHolder holder, CancellationToken ct);

    /// <summary>All accounts held by the customer.</summary>
    Task<IReadOnlyList<AccountDetail>> GetAccountsAsync(string customerId, CancellationToken ct);

    /// <summary>Opens an account in the given currency; returns its number, or null when refused.</summary>
    Task<string?> CreateAccountAsync(string customerId, string currency, CancellationToken ct);

    /// <summary>Current exchange rates published by the provider.</summary>
    Task<IReadOnlyList<CurrencyRate>> GetRatesAsync(CancellationToken ct);
}

public class AccountProviderException(string message, Exception? inner = null) : Exception(message, inner);
