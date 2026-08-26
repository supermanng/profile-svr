using Refit;

namespace ProfileSvr.Common.Accounts;

/// <summary>
/// OneCore (Digitvant core-banking) implementation of <see cref="IAccountProvider"/> over the
/// Refit <see cref="IOneCoreApi"/> client — the same provider and endpoints vliquidity uses:
///   GET  /api/v1/customers?Search={email}            — find customer by email
///   POST /api/v1/customers/create                    — create customer (CIF)
///   GET  /api/v1/customer-accounts?CustomerCif={cif} — list accounts
///   POST /api/v1/customer-accounts/create            — open account (product code per currency)
///   GET  /api/v1/currencies/rates                    — exchange rates
/// Config: OneCore:{BaseUrl, SsoBaseUrl, ClientId, ClientSecret, OfficeId,
///          AccountOfficerId, NairaProductId, CadProductId}.
/// </summary>
public class OneCoreAccountProvider(IOneCoreApi api, IConfiguration configuration) : IAccountProvider
{
    private static OneCoreEnvelope<T> Unwrap<T>(IApiResponse<OneCoreEnvelope<T>> response, string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            // Surface the provider's error body — it carries the actual rejection reason.
            var detail = response.Error?.Content;
            throw new AccountProviderException(
                $"The core-banking service returned {(int)response.StatusCode} for {operation}." +
                (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" Response: {Truncate(detail)}"));
        }
        return response.Content
            ?? throw new AccountProviderException(
                $"The core-banking service returned an empty body for {operation}.");
    }

    private static string Truncate(string value) =>
        value.Length > 500 ? value[..500] + "…" : value;

    public async Task<string?> GetOrCreateCustomerAsync(AccountHolder holder, CancellationToken ct)
    {
        // Idempotent: OneCore is searched by email before creating (mirrors vliquidity).
        var existing = Unwrap(await api.GetCustomersAsync(holder.Email, ct), "the customer search");
        var found = existing.Data?.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Cif));
        if (found is not null)
            return found.Cif;

        // OneCore requires a gender; the KYC record rarely carries one. Default to Male for
        // the customer payload only — the stored profile keeps its real value (or null).
        var gender = string.IsNullOrWhiteSpace(holder.Gender) ? "Male" : holder.Gender;

        var created = Unwrap(await api.CreateCustomerAsync(new OneCoreCreateCustomerRequest(
            OfficeCode: configuration["OneCore:OfficeId"],
            AccountOfficerStaffId: configuration["OneCore:AccountOfficerId"],
            AccessLevel: 1,
            TierLevel: 1,
            IsTaxExempt: true,
            FirstName: holder.FirstName,
            LastName: holder.LastName,
            OtherNames: holder.MiddleName,
            Title: string.Equals(gender, "Male", StringComparison.OrdinalIgnoreCase) ? "Mr" : "Mrs",
            Gender: gender,
            DateOfBirth: holder.DateOfBirth?.ToString("yyyy-MM-dd"),
            Email: holder.Email,
            PhoneNumber: holder.PhoneNumber,
            Bvn: holder.Bvn,
            Nin: holder.Nin,
            Nationality: "Nigerian",
            Occupation: "",
            Address: "",
            EmployerAddress: "",
            EmployerName: "",
            Hometown: "",
            Lga: "",
            MaritalStatus: "",
            MeansOfIdentification: "",
            MeansOfIdentificationNumber: "",
            OfficePhoneNumber: "",
            Religion: "",
            State: "",
            Tin: ""), ct), "the customer creation");

        if (!created.Success)
            throw new AccountProviderException(
                $"The core-banking service refused the customer creation: " +
                $"{created.Message ?? "no message"}{FormatErrors(created.Errors)}");
        return created.Data?.CustomerId;
    }

    private static string FormatErrors(List<string>? errors) =>
        errors is { Count: > 0 } ? $" ({string.Join("; ", errors)})" : string.Empty;

    public async Task<IReadOnlyList<AccountDetail>> GetAccountsAsync(string customerId, CancellationToken ct)
    {
        var response = Unwrap(await api.GetCustomerAccountsAsync(customerId, ct), "the account list");
        return response.Data?
            .Select(a => new AccountDetail(a.AccountName, a.AccountNumber, a.CurrencyCode, a.WithdrawableBalance))
            .ToList() ?? [];
    }

    public async Task<string?> CreateAccountAsync(string customerId, string currency, CancellationToken ct)
    {
        var productCode = currency switch
        {
            AccountCurrencies.Naira => configuration["OneCore:NairaProductId"],
            AccountCurrencies.CanadianDollar => configuration["OneCore:CadProductId"],
            _ => null
        };
        if (string.IsNullOrWhiteSpace(productCode))
            throw new AccountProviderException($"No OneCore product id is configured for {currency} accounts.");

        var response = Unwrap(await api.CreateAccountAsync(new OneCoreCreateAccountRequest(
            OfficeCode: configuration["OneCore:OfficeId"],
            CustomerCif: customerId,
            ProductCode: productCode,
            AccessLevel: 5,
            AccountTierLevel: 1,
            AccountType: "Savings",
            CategoryOfAccount: "Individual",
            MinimumBalanceRequired: 0,
            EnableEmailNotification: true,
            EnableSmsNotification: true,
            StatementDeliveryFrequency: "Monthly",
            StatementDeliveryMode: "Email",
            GroupConnectionId: ""), ct), $"the {currency} account creation");

        if (!response.Success)
            throw new AccountProviderException(
                $"The core-banking service refused the {currency} account creation: " +
                $"{response.Message ?? "no message"}{FormatErrors(response.Errors)}");
        return response.Data?.AccountNumber;
    }

    public async Task<IReadOnlyList<CurrencyRate>> GetRatesAsync(CancellationToken ct)
    {
        var response = Unwrap(await api.GetRatesAsync(ct), "the rates lookup");
        return response.Data?
            .Where(r => !string.IsNullOrWhiteSpace(r.SourceCode) && !string.IsNullOrWhiteSpace(r.TargetCode))
            .Select(r => new CurrencyRate(
                r.SourceCode!, r.TargetCode!, r.Type, r.BuyRate, r.SellRate, r.Date, r.UpdatedAt ?? r.CreatedAt))
            .ToList() ?? [];
    }
}
