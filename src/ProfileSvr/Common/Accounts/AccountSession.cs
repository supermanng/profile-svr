using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Common.Accounts;

/// <summary>Shared login/refresh step: self-heal missing accounts, then load accounts + rates.</summary>
public static class AccountSession
{
    /// <summary>
    /// Retries provisioning for a verified profile whose accounts are missing (persisting any
    /// changes), then loads the account list and exchange rates. Best-effort: provider failures
    /// yield empty lists and never fail the calling endpoint.
    /// </summary>
    public static async Task<(IReadOnlyList<AccountDetail> Accounts, IReadOnlyList<CurrencyRate> Rates)> LoadAsync(
        AppDbContext db, IAccountFacade accounts, Profile profile, CancellationToken ct)
    {
        if (await accounts.EnsureAccountsAsync(profile, ct))
        {
            profile.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        // Independent HTTP calls — run them concurrently (neither touches the DbContext).
        var accountsTask = accounts.GetAccountsAsync(profile, ct);
        var ratesTask = accounts.GetRatesAsync(ct);
        return (await accountsTask, await ratesTask);
    }
}
