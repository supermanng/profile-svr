using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Features.Webhooks;

/// <summary>
/// Inbound credit notifications from the virtual-account provider (same contract as
/// vliquidity's /webhook/virtual-account-credit): the credit is stored (idempotent on
/// transactionRef) and the PostCbaCreditsJob settles it into the CBA naira account.
/// When DigitVirtual:WebhookSecret is configured, the X-Signature header must carry
/// lowercase-hex HMAC-SHA256 of the raw body; when unset, verification is skipped (dev).
/// A storage failure returns 500 so the provider redelivers instead of losing the credit.
/// </summary>
public static class VirtualAccountCreditWebhook
{
    private record Payload(
        string? TransactionRef,
        decimal Amount,
        string? Account,
        string? SourceAccount,
        string? SourceBank,
        string? SenderName,
        string? Narration,
        DateTime? TransactionDate,
        string? Status);

    private static readonly JsonSerializerOptions CaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/webhook/virtual-account-credit", Handle)
                .WithSummary("Receive a virtual-account credit notification (stored, then posted to the CBA by the background job)")
                .WithTags("Webhooks");
    }

    private static async Task<IResult> Handle(
        HttpRequest request,
        AppDbContext db,
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("VirtualAccountCreditWebhook");

        string body;
        using (var reader = new StreamReader(request.Body, Encoding.UTF8))
            body = await reader.ReadToEndAsync(ct);

        // Signature check — enforced only when a webhook secret is configured.
        var secret = configuration["DigitVirtual:WebhookSecret"];
        if (!string.IsNullOrWhiteSpace(secret))
        {
            var signature = request.Headers["X-Signature"].FirstOrDefault();
            if (!SignatureMatches(body, secret, signature))
            {
                logger.LogWarning("Virtual-account credit webhook rejected: invalid signature");
                return ApiResults.Fail(StatusCodes.Status401Unauthorized, "Invalid webhook signature.");
            }
        }

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(body, CaseInsensitive);
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload is null ||
            string.IsNullOrWhiteSpace(payload.TransactionRef) ||
            string.IsNullOrWhiteSpace(payload.Account))
        {
            // Redelivery can't fix a malformed notification — acknowledge and drop it.
            logger.LogWarning("Invalid virtual-account credit webhook payload: {Payload}", body);
            return ApiResults.Ok(new { status = "ignored", receivedAtUtc = DateTime.UtcNow },
                "The notification is missing transactionRef or account.");
        }

        logger.LogInformation(
            "Credit notification: account {Account} credited {Amount} (ref {TransactionRef}, status {Status})",
            payload.Account, payload.Amount, payload.TransactionRef, payload.Status);

        // Idempotent: the provider may re-deliver the same credit.
        var reference = payload.TransactionRef.Trim();
        var duplicate = await db.VirtualAccountCredits.AnyAsync(c => c.TransactionRef == reference, ct);
        if (!duplicate)
        {
            db.VirtualAccountCredits.Add(new VirtualAccountCredit
            {
                Id = Guid.NewGuid(),
                TransactionRef = reference,
                VirtualAccount = payload.Account.Trim(),
                Amount = payload.Amount,
                SourceAccount = payload.SourceAccount,
                SourceBank = payload.SourceBank,
                SenderName = payload.SenderName,
                Narration = payload.Narration,
                TransactionDate = payload.TransactionDate ?? DateTime.UtcNow,
                Status = payload.Status,
                CreatedAtUtc = DateTime.UtcNow
            });

            try
            {
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Credit stored for posting (ref {TransactionRef})", reference);
            }
            catch (DbUpdateException)
            {
                // Two simultaneous deliveries raced on the unique index — the credit is stored.
                logger.LogInformation("Duplicate credit webhook ignored on save (ref {TransactionRef})", reference);
            }
        }
        else
        {
            logger.LogInformation("Duplicate credit webhook ignored (ref {TransactionRef})", reference);
        }

        return ApiResults.Ok(new { status = "received", receivedAtUtc = DateTime.UtcNow });
    }

    private static bool SignatureMatches(string body, string secret, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return false;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signature.Trim().ToLowerInvariant()));
    }
}
