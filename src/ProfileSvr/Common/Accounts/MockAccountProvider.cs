using System.Security.Cryptography;
using System.Text;

namespace ProfileSvr.Common.Accounts;

/// <summary>
/// Dev fallback used when OneCore:BaseUrl is not configured — deterministic in-memory
/// customers/accounts and static NGN/CAD rates. Never use in production.
/// </summary>
public class MockAccountProvider(ILogger<MockAccountProvider> logger) : IAccountProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<AccountDetail>> _accountsByCif = [];

    public Task<string?> GetOrCreateCustomerAsync(AccountHolder holder, CancellationToken ct)
    {
        var cif = "CIF" + Digits(holder.Email, 8);
        logger.LogWarning("[DEV ONLY] Mock banking customer {Cif} for {Email}", cif, holder.Email);
        return Task.FromResult<string?>(cif);
    }

    public Task<IReadOnlyList<AccountDetail>> GetAccountsAsync(string customerId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<AccountDetail>>(
                _accountsByCif.TryGetValue(customerId, out var accounts) ? [.. accounts] : []);
        }
    }

    public Task<string?> CreateAccountAsync(string customerId, string currency, CancellationToken ct)
    {
        // Deterministic per (customer, currency) so repeated calls behave get-or-create.
        var accountNumber = "2" + Digits(customerId + currency, 9);
        lock (_gate)
        {
            var accounts = _accountsByCif.TryGetValue(customerId, out var list)
                ? list
                : _accountsByCif[customerId] = [];
            if (!accounts.Any(a => a.AccountNumber == accountNumber))
                accounts.Add(new AccountDetail($"Mock {currency} Account", accountNumber, currency, 0m));
        }
        logger.LogWarning("[DEV ONLY] Mock {Currency} account {AccountNumber} for {Cif}",
            currency, accountNumber, customerId);
        return Task.FromResult<string?>(accountNumber);
    }

    public Task<IReadOnlyList<CurrencyRate>> GetRatesAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        return Task.FromResult<IReadOnlyList<CurrencyRate>>(
        [
            new CurrencyRate("CAD", "NGN", "FX", 1080m, 1050m, now, now),
            new CurrencyRate("NGN", "CAD", "FX", 0.00095m, 0.00090m, now, now)
        ]);
    }

    private static string Digits(string seed, int count)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(seed));
        var builder = new StringBuilder(count);
        foreach (var b in hash)
        {
            builder.Append(b % 10);
            if (builder.Length == count)
                break;
        }
        return builder.ToString();
    }
}
