using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProfileSvr.Common.Accounts;

/// <summary>Posts an incoming virtual-account credit into the CBA (naira) deposit account.</summary>
public interface ICbaCreditPoster
{
    /// <summary>
    /// Posts the credit; returns the raw provider response body. Throws
    /// <see cref="HttpRequestException"/> (carrying the status code) on a non-2xx response,
    /// except when the CBA reports the reference as a duplicate — that is settled success.
    /// </summary>
    Task<string> PostCreditAsync(
        string reference,
        string cbaAccountNumber,
        decimal amount,
        decimal charge,
        decimal providerCharge,
        string? sender,
        CancellationToken ct);
}

/// <summary>
/// OneCore integrations-webhook implementation (exactly as vliquidity posts naira credits):
///   POST {OneCore:WebhookUrl}
///   headers: X-Integration-Key: {OneCore:TransferIntegrationKey}
///            X-Signature: lowercase hex HMAC-SHA256(rawBody, OneCore:TransferIntegrationSecret)
///   body: { reference, accountNumber, amount, charge, providerCharge, narration }
/// Deliberately a raw HttpClient rather than Refit: the signature is computed over the exact
/// serialized body bytes, so this code must own the serialization.
/// </summary>
public class CbaCreditPoster(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<CbaCreditPoster> logger) : ICbaCreditPoster
{
    public async Task<string> PostCreditAsync(
        string reference,
        string cbaAccountNumber,
        decimal amount,
        decimal charge,
        decimal providerCharge,
        string? sender,
        CancellationToken ct)
    {
        var webhookUrl = configuration["OneCore:WebhookUrl"]
            ?? throw new InvalidOperationException("OneCore:WebhookUrl is not configured.");
        var integrationKey = configuration["OneCore:TransferIntegrationKey"]
            ?? throw new InvalidOperationException("OneCore:TransferIntegrationKey is not configured.");
        var integrationSecret = configuration["OneCore:TransferIntegrationSecret"]
            ?? throw new InvalidOperationException("OneCore:TransferIntegrationSecret is not configured.");

        var narration = configuration["OneCore:VirtualAccountCreditNarration"] ?? "Transfer Received";
        if (!string.IsNullOrWhiteSpace(sender))
            narration = $"{narration} from {sender.Trim()}";

        var rawBody = JsonSerializer.Serialize(new
        {
            reference,
            accountNumber = cbaAccountNumber,
            amount,
            charge,
            providerCharge,
            narration
        });

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(integrationSecret));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody)))
            .ToLowerInvariant();

        using var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl)
        {
            Content = new StringContent(rawBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Integration-Key", integrationKey);
        request.Headers.Add("X-Signature", signature);

        logger.LogInformation(
            "Posting CBA credit. Reference: {Reference}, Account: {Account}, Amount: {Amount}",
            reference, cbaAccountNumber, amount);

        var client = httpClientFactory.CreateClient();
        using var response = await client.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        logger.LogInformation(
            "CBA credit response. Reference: {Reference}, Status: {Status}, Body: {Body}",
            reference, (int)response.StatusCode, responseBody);

        // A duplicate means the CBA already credited this reference — treat as settled so the
        // job stops retrying an already-successful posting (idempotency via the reference).
        if (!response.IsSuccessStatusCode && IsDuplicate(response.StatusCode, responseBody))
        {
            logger.LogInformation(
                "CBA credit for {Reference} reported as duplicate/already-posted; treating as settled.",
                reference);
            return responseBody;
        }

        response.EnsureSuccessStatusCode();
        return responseBody;
    }

    private static bool IsDuplicate(System.Net.HttpStatusCode statusCode, string? body)
    {
        if ((int)statusCode == 409)
            return true;
        var text = body?.ToLowerInvariant() ?? string.Empty;
        return text.Contains("duplicate") || text.Contains("already");
    }
}
