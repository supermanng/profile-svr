using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using ProfileSvr.Common;
using ProfileSvr.Tests.Infrastructure;

namespace ProfileSvr.Tests.IntegrationTests;

public class EncryptionTests(TestAppFactory factory) : IClassFixture<TestAppFactory>
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    private HttpClient CreateEncryptedClient() =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Encryption:Enabled", "true");
            builder.UseSetting("Encryption:Key", Convert.ToBase64String(Key));
        }).CreateClient();

    [Fact]
    public async Task Health_IsExempt_StaysPlaintext()
    {
        var client = CreateEncryptedClient();
        var body = await client.GetStringAsync("/health");
        Assert.Contains("healthy", body); // readable without decryption
    }

    [Fact]
    public async Task CreditWebhook_IsExempt_AcceptsPlaintext()
    {
        var client = CreateEncryptedClient();
        // Provider webhooks arrive unencrypted; the path is exempt from payload encryption.
        var response = await client.PostAsJsonAsync("/webhook/virtual-account-credit",
            new { amount = 1m, status = "SUCCESSFUL" }); // malformed → acknowledged, not rejected as plaintext
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("ignored", raw); // readable plaintext response, not an encrypted envelope
    }

    [Fact]
    public async Task PlaintextRequest_IsRejected()
    {
        var client = CreateEncryptedClient();
        var response = await client.PostAsJsonAsync("/api/onboarding/initiate",
            new { emailAddress = "enc-reject@example.com" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // even the rejection body is encrypted
        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        Assert.True(doc.RootElement.TryGetProperty("data", out _));
    }

    [Fact]
    public async Task EncryptedRoundTrip_RequestDecrypted_ResponseEncrypted()
    {
        var client = CreateEncryptedClient();

        var plaintext = JsonSerializer.Serialize(new
        {
            emailAddress = "enc-flow@example.com",
            device = new
            {
                deviceIdentifier = "enc-device-1",
                name = "Encrypted Device",
                platform = "test",
                osName = "T", osVersion = "1", manufacturer = "T", model = "T", appVersion = "1"
            }
        });
        var envelope = new { data = PayloadCrypto.EncryptString(Key, plaintext) };

        var response = await client.PostAsJsonAsync("/api/onboarding/initiate", envelope);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Response is an encrypted envelope; decrypting yields the normal ApiResponse.
        var raw = await response.Content.ReadAsStringAsync();
        using var outer = JsonDocument.Parse(raw);
        var decrypted = PayloadCrypto.Decrypt(Key, outer.RootElement.GetProperty("data").GetString()!);
        Assert.NotNull(decrypted);

        using var inner = JsonDocument.Parse(decrypted!);
        Assert.True(inner.RootElement.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(16, inner.RootElement.GetProperty("data").GetProperty("retrievalCode").GetString()!.Length);
    }

    [Fact]
    public async Task TamperedEnvelope_IsRejected()
    {
        var client = CreateEncryptedClient();
        var good = PayloadCrypto.EncryptString(Key, """{"emailAddress":"x@y.com"}""");
        var tampered = Convert.FromBase64String(good);
        tampered[^1] ^= 0xFF;

        var response = await client.PostAsJsonAsync("/api/onboarding/initiate",
            new { data = Convert.ToBase64String(tampered) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
