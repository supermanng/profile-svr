using Refit;

namespace ProfileSvr.Common.Accounts;

/// <summary>Envelope every OneCore endpoint returns.</summary>
public record OneCoreEnvelope<T>(bool Success, string? Code, string? Message, List<string>? Errors, T? Data);

public record OneCoreCustomer(string? Id, string? Cif, string? FullName);

public record OneCoreCreatedCustomer(string? Id, string? CustomerId, string? CustomerFullName);

public record OneCoreCustomerAccount(
    string? AccountNumber, string? AccountName, string? CurrencyCode, decimal WithdrawableBalance);

public record OneCoreCreatedAccount(
    string? Id, string? CustomerId, string? AccountName, string? AccountNumber);

public record OneCoreRate(
    string? SourceCode, string? TargetCode, string? Type, decimal BuyRate, decimal SellRate,
    DateTime? Date, DateTime? CreatedAt, DateTime? UpdatedAt);

public record OneCoreCreateCustomerRequest(
    string? OfficeCode,
    string? AccountOfficerStaffId,
    int AccessLevel,
    int TierLevel,
    bool IsTaxExempt,
    string? FirstName,
    string? LastName,
    string? OtherNames,
    string? Title,
    string? Gender,
    string? DateOfBirth,
    string? Email,
    string? PhoneNumber,
    string? Bvn,
    string? Nin,
    string? Nationality,
    string? Occupation,
    string? Address,
    string? EmployerAddress,
    string? EmployerName,
    string? Hometown,
    string? Lga,
    string? MaritalStatus,
    string? MeansOfIdentification,
    string? MeansOfIdentificationNumber,
    string? OfficePhoneNumber,
    string? Religion,
    string? State,
    string? Tin);

public record OneCoreCreateAccountRequest(
    string? OfficeCode,
    string? CustomerCif,
    string? ProductCode,
    int AccessLevel,
    int AccountTierLevel,
    string? AccountType,
    string? CategoryOfAccount,
    decimal MinimumBalanceRequired,
    bool EnableEmailNotification,
    bool EnableSmsNotification,
    string? StatementDeliveryFrequency,
    string? StatementDeliveryMode,
    string? GroupConnectionId);

/// <summary>
/// OneCore (Digitvant core-banking) HTTP surface — the same endpoints vliquidity uses.
/// Refit serializes with web defaults (camelCase), matching OneCore's JSON.
/// Auth: <see cref="OneCoreAuthHandler"/> attaches a cached client-credentials bearer token.
/// </summary>
public interface IOneCoreApi
{
    [Get("/api/v1/customers")]
    Task<IApiResponse<OneCoreEnvelope<List<OneCoreCustomer>>>> GetCustomersAsync(
        [Query, AliasAs("Search")] string search, CancellationToken ct);

    [Post("/api/v1/customers/create")]
    Task<IApiResponse<OneCoreEnvelope<OneCoreCreatedCustomer>>> CreateCustomerAsync(
        [Body] OneCoreCreateCustomerRequest request, CancellationToken ct);

    [Get("/api/v1/customer-accounts")]
    Task<IApiResponse<OneCoreEnvelope<List<OneCoreCustomerAccount>>>> GetCustomerAccountsAsync(
        [Query, AliasAs("CustomerCif")] string customerCif, CancellationToken ct);

    [Post("/api/v1/customer-accounts/create")]
    Task<IApiResponse<OneCoreEnvelope<OneCoreCreatedAccount>>> CreateAccountAsync(
        [Body] OneCoreCreateAccountRequest request, CancellationToken ct);

    [Get("/api/v1/currencies/rates")]
    Task<IApiResponse<OneCoreEnvelope<List<OneCoreRate>>>> GetRatesAsync(CancellationToken ct);
}
