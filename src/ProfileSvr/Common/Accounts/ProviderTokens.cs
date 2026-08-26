using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Refit;

namespace ProfileSvr.Common.Accounts;

public record OAuthTokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

/// <summary>Client-credentials token endpoint of the OneCore SSO (OneCore:SsoBaseUrl).</summary>
public interface IOneCoreTokenApi
{
    [Post("/connect/token")]
    Task<IApiResponse<OAuthTokenResponse>> RequestTokenAsync(
        [Body(BodySerializationMethod.UrlEncoded)] Dictionary<string, string> form);
}

/// <summary>Client-credentials token endpoint of the VantPay SSO (DigitVirtual:SsoBaseUrl) — used for the virtual-account API.</summary>
public interface ISsoTokenApi
{
    [Post("/connect/token")]
    Task<IApiResponse<OAuthTokenResponse>> RequestTokenAsync(
        [Body(BodySerializationMethod.UrlEncoded)] Dictionary<string, string> form);
}

/// <summary>
/// Caches a client-credentials bearer token until shortly before it expires, so outbound
/// provider calls don't fetch a fresh token every time. Holds state only — the fetch is
/// supplied per call by the auth handler that owns the Refit token client.
/// </summary>
public class BearerTokenCache
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAtUtc;

    public async Task<string> GetOrFetchAsync(
        Func<Task<IApiResponse<OAuthTokenResponse>>> fetch, CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _expiresAtUtc)
            return _token;

        await _lock.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expiresAtUtc)
                return _token;

            using var response = await fetch();
            if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(response.Content?.AccessToken))
                throw new AccountProviderException(
                    $"The token endpoint returned {(int)response.StatusCode} or an empty access token.");

            _token = response.Content.AccessToken;
            // Refresh a minute early; never cache for less than a minute.
            _expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, response.Content.ExpiresIn - 60));
            return _token;
        }
        finally
        {
            _lock.Release();
        }
    }

    public static Dictionary<string, string> ClientCredentialsForm(string? clientId, string? clientSecret) => new()
    {
        ["grant_type"] = "client_credentials",
        ["client_id"] = clientId ?? string.Empty,
        ["client_secret"] = clientSecret ?? string.Empty
    };
}

/// <summary>Token state for the OneCore core-banking API.</summary>
public sealed class OneCoreTokenCache : BearerTokenCache;

/// <summary>Token state for the virtual-account (VantPay) merchant API.</summary>
public sealed class VirtualAccountTokenCache : BearerTokenCache;

/// <summary>Adds the OneCore bearer token to every outbound core-banking request.</summary>
public class OneCoreAuthHandler(
    IOneCoreTokenApi tokenApi,
    OneCoreTokenCache cache,
    IConfiguration configuration) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await cache.GetOrFetchAsync(
            () => tokenApi.RequestTokenAsync(BearerTokenCache.ClientCredentialsForm(
                configuration["OneCore:ClientId"], configuration["OneCore:ClientSecret"])),
            cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }
}

/// <summary>Adds the VantPay-SSO bearer token (DigitVirtual client) to virtual-account API requests.</summary>
public class VirtualAccountAuthHandler(
    ISsoTokenApi tokenApi,
    VirtualAccountTokenCache cache,
    IConfiguration configuration) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await cache.GetOrFetchAsync(
            () => tokenApi.RequestTokenAsync(BearerTokenCache.ClientCredentialsForm(
                configuration["DigitVirtual:ClientId"], configuration["DigitVirtual:ClientSecret"])),
            cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }
}
