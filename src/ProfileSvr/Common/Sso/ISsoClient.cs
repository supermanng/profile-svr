namespace ProfileSvr.Common.Sso;

public interface ISsoClient
{
    /// <summary>Returns true when a user with this email is registered on the SSO service.</summary>
    Task<bool> EmailExistsAsync(string email, CancellationToken ct);

    /// <summary>
    /// Creates the SSO user. Throws SsoException when the SSO rejects the registration.
    /// </summary>
    Task CreateUserAsync(string username, string password, string email, string sourceId, CancellationToken ct);

    /// <summary>Password-grant login. Throws SsoException when credentials are rejected.</summary>
    Task<SsoTokens> PasswordLoginAsync(string username, string password, CancellationToken ct);

    /// <summary>Exchanges a refresh token for new tokens. Throws SsoException when rejected.</summary>
    Task<SsoTokens> RefreshTokenAsync(string refreshToken, CancellationToken ct);

    /// <summary>Obtains a password-reset token for the user. Throws SsoException when rejected.</summary>
    Task<string> InitiatePasswordResetAsync(string username, CancellationToken ct);

    /// <summary>Resets the password using a reset token. Throws SsoException when rejected.</summary>
    Task ResetPasswordAsync(string username, string passwordResetToken, string newPassword, CancellationToken ct);

    /// <summary>Changes the password using the current one. Throws SsoException when rejected.</summary>
    Task ChangePasswordAsync(string username, string currentPassword, string newPassword, CancellationToken ct);

    /// <summary>
    /// Sets the user's "type" claim (onboarding | temporary | profile-active) so issued JWTs
    /// carry it. Replaces any previous value. Best-effort — throws SsoException on hard failure.
    /// </summary>
    Task SetTypeClaimAsync(string username, string value, CancellationToken ct);
}

public record SsoTokens(
    string AccessToken,
    string? RefreshToken,
    string? IdToken,
    string TokenType,
    int ExpiresIn);

public class SsoException(string message) : Exception(message);
