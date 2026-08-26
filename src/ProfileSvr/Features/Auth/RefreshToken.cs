using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentValidation;
using Microsoft.IdentityModel.Tokens;
using ProfileSvr.Common;
using ProfileSvr.Common.Accounts;
using ProfileSvr.Common.Sso;
using ProfileSvr.Database;
using ProfileSvr.Domain;

namespace ProfileSvr.Features.Auth;

/// <summary>
/// Exchanges a refresh token for new tokens. Like login, the response carries the profile,
/// its accounts and the current rates (resolved from the new access token's claims), and any
/// missing account provisioning is retried here — so a provisioning failure at profile
/// creation self-heals the next time the session is refreshed (mirrors vliquidity).
/// </summary>
public static class RefreshToken
{
    public record Request(string Token);

    private record Response(
        string AccessToken,
        string? RefreshToken,
        string? IdToken,
        string TokenType,
        int ExpiresIn,
        string Type,
        ProfileDto? Profile,
        IReadOnlyList<AccountDetail>? Accounts,
        IReadOnlyList<CurrencyRate>? Rates);

    public class Validator : AbstractValidator<Request>
    {
        public Validator()
        {
            RuleFor(r => r.Token).NotEmpty();
        }
    }

    public class Endpoint : IEndpoint
    {
        public static void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/auth/refresh", Handle)
                .WithSummary("Exchange a refresh token for a new access token (returns profile, accounts and rates)")
                .WithTags("Auth");
    }

    private static async Task<IResult> Handle(
        Request request,
        AppDbContext db,
        ISsoClient sso,
        IAccountFacade accountFacade,
        IValidator<Request> validator,
        CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ApiResults.ValidationFailed(validation);

        SsoTokens tokens;
        try
        {
            tokens = await sso.RefreshTokenAsync(request.Token, ct);
        }
        catch (SsoException ex)
        {
            return ApiResults.Fail(StatusCodes.Status401Unauthorized, ex.Message);
        }
        catch (HttpRequestException)
        {
            return ApiResults.Fail(StatusCodes.Status503ServiceUnavailable,
                "Could not reach the SSO service. Try again later.");
        }

        // The new access token came straight from the SSO over TLS, so its claims are
        // trusted without re-validating the signature here.
        var profile = await ResolveProfileAsync(tokens.AccessToken, db, ct);

        ProfileDto? profileDto = null;
        IReadOnlyList<AccountDetail>? accounts = null;
        IReadOnlyList<CurrencyRate>? rates = null;
        if (profile is not null)
        {
            // Retry any missing account provisioning, then load accounts + rates (best-effort).
            (accounts, rates) = await AccountSession.LoadAsync(db, accountFacade, profile, ct);
            profileDto = await ProfileDtoMapper.BuildAsync(db, profile, ct);
        }

        return ApiResults.Ok(new Response(
            tokens.AccessToken, tokens.RefreshToken, tokens.IdToken,
            tokens.TokenType, tokens.ExpiresIn,
            TokenTypes.ForProfile(profile),
            profileDto, accounts, rates),
            "Token refreshed.");
    }

    private static async Task<Profile?> ResolveProfileAsync(string accessToken, AppDbContext db, CancellationToken ct)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(accessToken))
                return null;
            var jwt = handler.ReadJwtToken(accessToken);
            var principal = new ClaimsPrincipal(new ClaimsIdentity(jwt.Claims));
            return await ProfileClaims.ResolveProfileAsync(principal, db, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or SecurityTokenException)
        {
            // Opaque or malformed access token — return the tokens without profile enrichment.
            return null;
        }
    }
}
