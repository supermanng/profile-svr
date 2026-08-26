using Microsoft.EntityFrameworkCore;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Common.Accounts;

/// <summary>
/// Facade over account provisioning and retrieval — endpoints only ever talk to this,
/// so the banks behind it can be replaced by swapping <see cref="IAccountProvider"/>
/// (core banking) and <see cref="IVirtualAccountProvider"/> (collections).
/// Mirrors vliquidity's post-KYC provisioning + login/refresh self-heal:
/// customer (CIF) → NGN account → CAD account → virtual account, each get-or-create and
/// best-effort. The virtual → naira mapping row that the credit-posting job resolves is
/// staged on the injected DbContext; the caller's SaveChanges persists it with the profile.
/// </summary>
public interface IAccountFacade
{
    /// <summary>
    /// Makes sure the banking customer (CIF) and the NGN + CAD + virtual accounts exist for
    /// a KYC-verified profile — the provider requires a BVN/NIN, so complete-kyc is the
    /// creation point and create-profile/login/refresh are the retry points. Mutates the
    /// profile fields; never throws — failures are logged and reflected as
    /// KycStatus=ProvisioningFailed so the next call retries. Returns true when any profile
    /// field changed (caller persists).
    /// </summary>
    Task<bool> EnsureAccountsAsync(Profile profile, CancellationToken ct);

    /// <summary>The profile's accounts with live balances; empty when unprovisioned or the provider is down.</summary>
    Task<IReadOnlyList<AccountDetail>> GetAccountsAsync(Profile profile, CancellationToken ct);

    /// <summary>Latest exchange rate per currency pair; empty when the provider is down.</summary>
    Task<IReadOnlyList<CurrencyRate>> GetRatesAsync(CancellationToken ct);
}

public class AccountFacade(
    IAccountProvider provider,
    IVirtualAccountProvider virtualAccounts,
    AppDbContext db,
    ILogger<AccountFacade> logger) : IAccountFacade
{
    public async Task<bool> EnsureAccountsAsync(Profile profile, CancellationToken ct)
    {
        // Accounts are only provisioned once the identity (BVN or NIN) is verified — the
        // core-banking provider refuses customer creation without it, so complete-kyc is
        // where the accounts get created (mirrors vliquidity's post-approval provisioning).
        if (!profile.BvnIsVerified && !profile.NinIsVerified)
            return false;

        var before = (profile.Cif, profile.NairaAccount, profile.CadAccount,
            profile.VirtualAccount, profile.KycStatus);
        var mappingAdded = false;

        if (profile.Cif is null)
        {
            try
            {
                profile.Cif = await provider.GetOrCreateCustomerAsync(new AccountHolder(
                    profile.FirstName ?? string.Empty,
                    profile.LastName ?? string.Empty,
                    profile.MiddleName,
                    profile.Gender,
                    profile.DateOfBirth,
                    profile.EmailAddress,
                    profile.PhoneNumber,
                    profile.Bvn,
                    profile.Nin), ct);
            }
            catch (Exception ex) when (IsProviderFailure(ex))
            {
                logger.LogWarning(ex, "Banking customer creation failed for profile {ProfileId}", profile.Id);
            }
        }

        if (profile.Cif is not null && (profile.NairaAccount is null || profile.CadAccount is null))
        {
            IReadOnlyList<AccountDetail> existing = [];
            try
            {
                existing = await provider.GetAccountsAsync(profile.Cif, ct);
            }
            catch (Exception ex) when (IsProviderFailure(ex))
            {
                logger.LogWarning(ex, "Account lookup failed for profile {ProfileId}", profile.Id);
            }

            profile.NairaAccount ??= await EnsureAccountAsync(profile, existing, AccountCurrencies.Naira, ct);
            profile.CadAccount ??= await EnsureAccountAsync(profile, existing, AccountCurrencies.CanadianDollar, ct);
        }

        // Virtual (collection) account — opened once the NGN account exists, so incoming
        // credits always have a naira account to map to.
        if (profile.VirtualAccount is null && profile.NairaAccount is not null)
        {
            try
            {
                var created = await virtualAccounts.CreateVirtualAccountAsync(profile.Bvn, profile.Nin, ct);
                if (created is not null)
                {
                    profile.VirtualAccount = created.AccountNumber;
                    profile.VirtualAccountBank = created.BankName;
                    AddMapping(profile, created.AccountName, created.BankName);
                    mappingAdded = true;
                }
            }
            catch (Exception ex) when (IsProviderFailure(ex))
            {
                logger.LogWarning(ex, "Virtual account creation failed for profile {ProfileId}", profile.Id);
            }
        }
        else if (profile.VirtualAccount is not null && profile.NairaAccount is not null)
        {
            // Backfill a missing mapping (mirrors vliquidity's EnsureVirtualAccountMapping).
            var hasMapping = await db.VirtualAccountMappings
                .AnyAsync(m => m.VirtualAccount == profile.VirtualAccount, ct);
            if (!hasMapping)
            {
                AddMapping(profile, accountName: null, profile.VirtualAccountBank);
                mappingAdded = true;
            }
        }

        // Reflect the provisioning outcome on the KYC status so clients (and the
        // login/refresh self-heal) can tell a verified-but-unprovisioned profile apart.
        var complete = profile.Cif is not null &&
                       profile.NairaAccount is not null &&
                       profile.CadAccount is not null &&
                       profile.VirtualAccount is not null;
        if (complete && profile.KycStatus == KycVerificationStatus.ProvisioningFailed)
        {
            profile.KycStatus = KycVerificationStatus.Approved;
            profile.KycStatusReason = null;
        }
        else if (!complete && profile.KycStatus == KycVerificationStatus.Approved)
        {
            profile.KycStatus = KycVerificationStatus.ProvisioningFailed;
            profile.KycStatusReason =
                "Your identity was verified but we couldn't finish setting up your accounts. We'll retry automatically.";
        }

        return mappingAdded ||
               (profile.Cif, profile.NairaAccount, profile.CadAccount,
                   profile.VirtualAccount, profile.KycStatus) != before;
    }

    private void AddMapping(Profile profile, string? accountName, string? bank) =>
        db.VirtualAccountMappings.Add(new VirtualAccountMapping
        {
            Id = Guid.NewGuid(),
            ProfileId = profile.Id,
            VirtualAccount = profile.VirtualAccount!,
            CbaAccount = profile.NairaAccount!,
            AccountName = accountName,
            Bank = bank,
            CreatedAtUtc = DateTime.UtcNow
        });

    private async Task<string?> EnsureAccountAsync(
        Profile profile, IReadOnlyList<AccountDetail> existing, string currency, CancellationToken ct)
    {
        var found = existing.FirstOrDefault(a =>
            string.Equals(a.Currency, currency, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(a.AccountNumber));
        if (found is not null)
            return found.AccountNumber;

        try
        {
            return await provider.CreateAccountAsync(profile.Cif!, currency, ct);
        }
        catch (Exception ex) when (IsProviderFailure(ex))
        {
            logger.LogWarning(ex, "{Currency} account creation failed for profile {ProfileId}",
                currency, profile.Id);
            return null;
        }
    }

    public async Task<IReadOnlyList<AccountDetail>> GetAccountsAsync(Profile profile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile.Cif))
            return [];
        try
        {
            return await provider.GetAccountsAsync(profile.Cif, ct);
        }
        catch (Exception ex) when (IsProviderFailure(ex))
        {
            logger.LogWarning(ex, "Account retrieval failed for profile {ProfileId}", profile.Id);
            return [];
        }
    }

    public async Task<IReadOnlyList<CurrencyRate>> GetRatesAsync(CancellationToken ct)
    {
        try
        {
            var rates = await provider.GetRatesAsync(ct);
            // Latest rate per (source, target) pair — mirrors vliquidity's login behaviour.
            return rates
                .GroupBy(r => (r.SourceCode, r.TargetCode))
                .Select(g => g.OrderByDescending(r => r.UpdatedAtUtc ?? r.Date ?? DateTime.MinValue).First())
                .ToList();
        }
        catch (Exception ex) when (IsProviderFailure(ex))
        {
            logger.LogWarning(ex, "Exchange-rate retrieval failed");
            return [];
        }
    }

    // Timeouts surface as TaskCanceledException even when the request itself is not cancelled.
    private static bool IsProviderFailure(Exception ex) =>
        ex is AccountProviderException or HttpRequestException or TaskCanceledException;
}
