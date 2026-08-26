using System.Text;
using System.Text.Json;

namespace ProfileSvr.Common;

/// <summary>
/// End-to-end payload encryption (enabled via Encryption:Enabled):
///   requests with a JSON body must arrive as {"data": "base64(nonce||ciphertext||tag)"} —
///   the middleware decrypts it and hands the plaintext JSON to the endpoint;
///   responses are captured and re-wrapped in the same envelope.
/// Exempt paths (docs, health, provider webhooks) stay in plaintext so tooling and
/// external callers keep working.
/// </summary>
public class PayloadEncryptionMiddleware(
    RequestDelegate next,
    IConfiguration configuration,
    ILogger<PayloadEncryptionMiddleware> logger)
{
    private static readonly string[] ExemptPrefixes = ["/swagger", "/openapi", "/health", "/webhook"];

    private readonly bool _enabled = configuration.GetValue<bool>("Encryption:Enabled");
    private readonly byte[]? _key = configuration.GetValue<bool>("Encryption:Enabled")
        ? PayloadCrypto.ParseKey(configuration["Encryption:Key"])
        : null;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_enabled || IsExempt(context.Request.Path))
        {
            await next(context);
            return;
        }

        // ---- decrypt the request body ----
        if (context.Request.ContentLength > 0)
        {
            string raw;
            using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
                raw = await reader.ReadToEndAsync(context.RequestAborted);

            var plaintext = Unwrap(raw);
            if (plaintext is null)
            {
                logger.LogWarning("Rejected unencrypted or undecryptable payload on {Path}", context.Request.Path);
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new ApiResponse<object>(
                    false, null, "The request payload must be encrypted: {\"data\": \"<base64 ciphertext>\"}."));
                return;
            }

            var bodyBytes = Encoding.UTF8.GetBytes(plaintext);
            context.Request.Body = new MemoryStream(bodyBytes);
            context.Request.ContentLength = bodyBytes.Length;
            context.Request.ContentType = "application/json";
        }

        // ---- capture and encrypt the response body ----
        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next(context);

            context.Response.Body = originalBody;
            if (buffer.Length == 0)
                return;

            var envelope = JsonSerializer.Serialize(
                new { data = PayloadCrypto.Encrypt(_key!, buffer.ToArray()) });
            var envelopeBytes = Encoding.UTF8.GetBytes(envelope);

            context.Response.ContentType = "application/json";
            context.Response.ContentLength = envelopeBytes.Length;
            await originalBody.WriteAsync(envelopeBytes, context.RequestAborted);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private string? Unwrap(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.String)
                return null;

            return PayloadCrypto.Decrypt(_key!, data.GetString()!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsExempt(PathString path) =>
        ExemptPrefixes.Any(p => path.StartsWithSegments(p));
}
