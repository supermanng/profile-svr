using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Common.Accounts;

public interface IVirtualAccountCreditProcessor
{
    /// <summary>Posts up to batchSize pending credits into the CBA; returns how many settled.</summary>
    Task<int> ProcessPendingCreditsAsync(int batchSize, CancellationToken ct = default);
}

/// <summary>
/// Settles stored virtual-account credits into the CBA naira account (as vliquidity does):
/// picks unposted, unabandoned, SUCCESSFUL credits oldest-first, resolves the virtual → CBA
/// account mapping, and posts each through <see cref="ICbaCreditPoster"/>. Transient failures
/// retry every tick until VirtualAccountCredit:MaxPostAttempts is spent; permanent (4xx)
/// failures dead-letter immediately. A MySQL advisory lock keeps replicas from double-posting.
/// </summary>
public class VirtualAccountCreditProcessor(
    AppDbContext db,
    ICbaCreditPoster poster,
    IConfiguration configuration,
    ILogger<VirtualAccountCreditProcessor> logger) : IVirtualAccountCreditProcessor
{
    private const string SuccessfulStatus = "SUCCESSFUL";
    private const string CreditPostingLock = "profilesvr:cba-credit-posting";

    private readonly int _maxAttempts = configuration.GetValue("VirtualAccountCredit:MaxPostAttempts", 10);
    private readonly decimal _chargePercent =
        configuration.GetValue("VirtualAccountCredit:ChargePercent", 1.0m);
    private readonly decimal _providerChargePercent =
        configuration.GetValue("VirtualAccountCredit:ProviderChargePercent", 0.5m);

    public Task<int> ProcessPendingCreditsAsync(int batchSize, CancellationToken ct = default) =>
        RunExclusivelyAsync(async () =>
        {
            var pending = await db.VirtualAccountCredits
                .Where(x => !x.CreditPosted && !x.PostingAbandoned && x.Status == SuccessfulStatus)
                .OrderBy(x => x.TransactionDate)
                .Take(batchSize)
                .ToListAsync(ct);

            var processed = 0;
            foreach (var credit in pending)
            {
                try
                {
                    var mapping = await db.VirtualAccountMappings
                        .FirstOrDefaultAsync(m => m.VirtualAccount == credit.VirtualAccount, ct);

                    if (mapping is null || string.IsNullOrWhiteSpace(mapping.CbaAccount))
                    {
                        await FailAsync(credit, permanent: false,
                            $"No CBA mapping for virtual account {credit.VirtualAccount}", ct);
                        continue;
                    }

                    var charge = _chargePercent / 100m * credit.Amount;
                    var providerCharge = _providerChargePercent / 100m * credit.Amount;

                    var response = await poster.PostCreditAsync(
                        credit.TransactionRef, mapping.CbaAccount, credit.Amount,
                        charge, providerCharge, DescribeSender(credit), ct);

                    credit.MarkPosted(response);
                    await db.SaveChangesAsync(ct);
                    processed++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // shutting down — don't count this as a failed attempt
                }
                catch (Exception ex)
                {
                    await FailAsync(credit, IsPermanent(ex), ex.Message, ct, ex);
                }
            }

            return processed;
        }, ct);

    private async Task FailAsync(
        VirtualAccountCredit credit, bool permanent, string? error, CancellationToken ct, Exception? ex = null)
    {
        if (permanent)
            credit.Abandon(error);
        else
            credit.RecordFailure(error, _maxAttempts);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception saveEx)
        {
            // Couldn't persist the attempt — leave it for the next tick rather than crash the loop.
            logger.LogError(saveEx, "Failed to persist failure state for credit {Ref}", credit.TransactionRef);
            return;
        }

        if (credit.PostingAbandoned)
            logger.LogError(ex,
                "CBA credit dead-lettered for {Ref} after {Attempts} attempt(s){Permanent}: {Error}",
                credit.TransactionRef, credit.Attempts, permanent ? " [permanent error]" : "", error);
        else
            logger.LogWarning(ex,
                "CBA credit posting failed for {Ref} (attempt {Attempts}/{Max}); will retry: {Error}",
                credit.TransactionRef, credit.Attempts, _maxAttempts, error);
    }

    /// <summary>A 4xx (except 408 timeout / 429 rate-limit) will never succeed on retry.</summary>
    private static bool IsPermanent(Exception ex) =>
        ex is HttpRequestException { StatusCode: { } status }
        && (int)status is >= 400 and < 500
        && (int)status is not (408 or 429);

    private static string? DescribeSender(VirtualAccountCredit credit)
    {
        var parts = new[] { credit.SenderName, credit.SourceBank, credit.SourceAccount }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim());

        var sender = string.Join(" / ", parts);
        if (string.IsNullOrWhiteSpace(sender))
            return null;

        return sender.Length > 100 ? sender[..100] : sender;
    }

    // ---- cluster-wide advisory lock (MySQL GET_LOCK); other providers (tests) run unlocked ----

    private async Task<int> RunExclusivelyAsync(Func<Task<int>> body, CancellationToken ct)
    {
        if (db.Database.ProviderName?.Contains("MySql", StringComparison.OrdinalIgnoreCase) != true)
            return await body();

        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
            await connection.OpenAsync(ct);

        try
        {
            if (!await TryAcquireLockAsync(connection, ct))
            {
                logger.LogDebug("Advisory lock '{Lock}' held elsewhere; skipping this run.", CreditPostingLock);
                return 0;
            }

            try
            {
                return await body();
            }
            finally
            {
                await ReleaseLockAsync(connection);
            }
        }
        finally
        {
            if (openedHere)
                await connection.CloseAsync();
        }
    }

    private static async Task<bool> TryAcquireLockAsync(DbConnection connection, CancellationToken ct)
    {
        // GET_LOCK(name, 0): try once and return immediately — 1 = acquired, 0 = busy, NULL = error.
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT GET_LOCK(@name, 0)";
        AddParameter(cmd, "@name", CreditPostingLock);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null && result != DBNull.Value && Convert.ToInt64(result) == 1;
    }

    private static async Task ReleaseLockAsync(DbConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT RELEASE_LOCK(@name)";
        AddParameter(cmd, "@name", CreditPostingLock);
        await cmd.ExecuteScalarAsync();
    }

    private static void AddParameter(DbCommand cmd, string name, string value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        cmd.Parameters.Add(parameter);
    }
}
