using System.Net;
using System.Net.Http.Json;

namespace ProfileSvr.Common.Sso;

/// <summary>
/// Client for the Digitvant SSO (Auth Service).
/// GET /api/users/user-with-email-exists/{email} returns a BooleanApiResponse:
///   200 { isSuccess: true,  data: true }  — user exists
///   400 { isSuccess: false, data: false, message: "User not found on our system" } — user does not exist
/// </summary>
public class SsoClient(HttpClient http, IConfiguration configuration) : ISsoClient
{
    // Cached service token for the pre-user endpoints (email-exists, create-user), which the SSO
    // now requires auth on. Shared across requests; refreshed a minute before expiry.
    private static string? _serviceToken;
    private static DateTime _serviceTokenExpiry = DateTime.MinValue;
    private static readonly SemaphoreSlim ServiceTokenLock = new(1, 1);

    private record BooleanApiResponse(bool IsSuccess, bool Data, string? Message);

    private record RegistrationModel(string Username, string Password, string Email, string SourceId);

    /// <summary>
    /// Bearer token for the endpoints that run before a user token exists (email-exists, create-user).
    /// Priority: a statically configured Sso:ServiceToken, else a cached client-credentials token.
    /// Returns null when neither is available (calls then go unauthenticated).
    /// </summary>
    private async Task<string?> GetServiceTokenAsync(CancellationToken ct)
    {
        var staticToken = configuration["Sso:ServiceToken"];
        if (!string.IsNullOrWhiteSpace(staticToken))
            return staticToken;

        if (_serviceToken is not null && DateTime.UtcNow < _serviceTokenExpiry)
            return _serviceToken;

        await ServiceTokenLock.WaitAsync(ct);
        try
        {
            if (_serviceToken is not null && DateTime.UtcNow < _serviceTokenExpiry)
                return _serviceToken;

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = configuration["Sso:ServiceClientId"] ?? configuration["Sso:ClientId"] ?? "",
                ["client_secret"] = configuration["Sso:ServiceClientSecret"] ?? configuration["Sso:ClientSecret"] ?? ""
            };
            var response = await http.PostAsync("/connect/token", new FormUrlEncodedContent(form), ct);
            var body = response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<TokenResponse>(ct)
                : null;

            if (body?.AccessToken is null)
                return null; // client_credentials not enabled for this client — caller falls back

            _serviceToken = body.AccessToken;
            _serviceTokenExpiry = DateTime.UtcNow.AddSeconds(Math.Max(30, body.ExpiresIn - 60));
            return _serviceToken;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        finally
        {
            ServiceTokenLock.Release();
        }
    }

    private async Task<HttpRequestMessage> WithServiceTokenAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await GetServiceTokenAsync(ct);
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    public async Task<bool> EmailExistsAsync(string email, CancellationToken ct)
    {
        using var request = await WithServiceTokenAsync(new HttpRequestMessage(
            HttpMethod.Get, $"/api/users/user-with-email-exists/{Uri.EscapeDataString(email)}"), ct);
        var response = await http.SendAsync(request, ct);

        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.BadRequest))
            throw new HttpRequestException(
                $"SSO returned unexpected status {(int)response.StatusCode} for email-exists check.");

        var body = await response.Content.ReadFromJsonAsync<BooleanApiResponse>(ct)
            ?? throw new HttpRequestException("SSO returned an empty body for email-exists check.");

        return body.Data;
    }

    public async Task CreateUserAsync(
        string username, string password, string email, string sourceId, CancellationToken ct)
    {
        var clientId = configuration["Sso:ClientId"]
            ?? throw new InvalidOperationException("Sso:ClientId is not configured.");

        using var request = await WithServiceTokenAsync(new HttpRequestMessage(
            HttpMethod.Post, $"/api/users/{Uri.EscapeDataString(clientId)}/client/create")
        {
            Content = System.Net.Http.Json.JsonContent.Create(
                new RegistrationModel(username, password, email, sourceId))
        }, ct);
        var response = await http.SendAsync(request, ct);

        // The response body shape varies (data may not be a boolean) — parse defensively.
        var success = response.IsSuccessStatusCode;
        string? message = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("isSuccess", out var flag) ||
                root.TryGetProperty("isSuccessful", out flag))
                success = success && flag.ValueKind == System.Text.Json.JsonValueKind.True;
            if (root.TryGetProperty("message", out var msg) &&
                msg.ValueKind == System.Text.Json.JsonValueKind.String)
                message = msg.GetString();
        }
        catch (System.Text.Json.JsonException)
        {
            // non-JSON body — fall back to the HTTP status alone
        }

        if (!success)
            throw new SsoException(
                message ?? $"SSO user creation failed with status {(int)response.StatusCode}.");
    }

    public Task<SsoTokens> PasswordLoginAsync(string username, string password, CancellationToken ct) =>
        RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = password,
            ["scope"] = "openid offline_access"
        }, "Invalid username or password.", ct);

    public Task<SsoTokens> RefreshTokenAsync(string refreshToken, CancellationToken ct) =>
        RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        }, "The refresh token is invalid or expired.", ct);

    private record TokenResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string? AccessToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("id_token")] string? IdToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("token_type")] string? TokenType,
        [property: System.Text.Json.Serialization.JsonPropertyName("expires_in")] int ExpiresIn,
        [property: System.Text.Json.Serialization.JsonPropertyName("error")] string? Error,
        [property: System.Text.Json.Serialization.JsonPropertyName("error_description")] string? ErrorDescription);

    public async Task<string> InitiatePasswordResetAsync(string username, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync("/api/users/initiate-reset", new { username }, ct);
        var (success, message, data) = await ParseFlexibleAsync(response, ct);

        var token = data.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    data.TryGetProperty("passwordResetToken", out var t)
            ? t.GetString()
            : null;

        if (!success || string.IsNullOrEmpty(token))
            throw new SsoException(message ?? "Could not initiate the password reset on the SSO.");

        return token;
    }

    public async Task ResetPasswordAsync(
        string username, string passwordResetToken, string newPassword, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync("/api/users/reset-password",
            new { username, passwordResetToken, newPassword }, ct);
        var (success, message, _) = await ParseFlexibleAsync(response, ct);
        if (!success)
            throw new SsoException(message ?? "The SSO rejected the password reset.");
    }

    public async Task ChangePasswordAsync(
        string username, string currentPassword, string newPassword, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync("/api/users/change-password",
            new { username, currentPassword, newPassword }, ct);
        var (success, message, _) = await ParseFlexibleAsync(response, ct);
        if (!success)
            throw new SsoException(message ?? "The SSO rejected the password change.");
    }

    public async Task SetTypeClaimAsync(string username, string value, CancellationToken ct)
    {
        // The service token can manage any user's claims; create replaces the value in place.
        using var request = await WithServiceTokenAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/users/claim/create")
            {
                Content = System.Net.Http.Json.JsonContent.Create(
                    new { username, claimType = TokenTypes.ClaimName, claimValue = value })
            }, ct);

        var response = await http.SendAsync(request, ct);
        var (success, message, _) = await ParseFlexibleAsync(response, ct);
        if (!success)
            throw new SsoException(message ?? "The SSO rejected the type claim update.");
    }

    /// <summary>Tolerant parse of the SSO's ApiResponse variants: (isSuccess[full], message, data).</summary>
    private static async Task<(bool Success, string? Message, System.Text.Json.JsonElement Data)>
        ParseFlexibleAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var success = response.IsSuccessStatusCode;
        string? message = null;
        var data = default(System.Text.Json.JsonElement);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("isSuccess", out var flag) ||
                root.TryGetProperty("isSuccessful", out flag))
                success = success && flag.ValueKind == System.Text.Json.JsonValueKind.True;
            if (root.TryGetProperty("message", out var msg) &&
                msg.ValueKind == System.Text.Json.JsonValueKind.String)
                message = msg.GetString();
            if (root.TryGetProperty("data", out var d))
                data = d.Clone();
        }
        catch (System.Text.Json.JsonException)
        {
            // non-JSON body — status code alone decides
        }
        return (success, message, data);
    }

    private async Task<SsoTokens> RequestTokenAsync(
        Dictionary<string, string> form, string rejectionMessage, CancellationToken ct)
    {
        form["client_id"] = configuration["Sso:ClientId"]
            ?? throw new InvalidOperationException("Sso:ClientId is not configured.");
        form["client_secret"] = configuration["Sso:ClientSecret"] ?? "";

        var response = await http.PostAsync("/connect/token", new FormUrlEncodedContent(form), ct);

        TokenResponse? body = null;
        try
        {
            body = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
        }
        catch (System.Text.Json.JsonException)
        {
            // non-JSON body — handled below
        }

        if (!response.IsSuccessStatusCode || body?.AccessToken is null)
            throw new SsoException(body?.ErrorDescription ?? rejectionMessage);

        return new SsoTokens(
            body.AccessToken, body.RefreshToken, body.IdToken,
            body.TokenType ?? "Bearer", body.ExpiresIn);
    }
}
