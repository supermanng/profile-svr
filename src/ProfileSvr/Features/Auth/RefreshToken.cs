using FluentValidation;
using ProfileSvr.Common;
using ProfileSvr.Common.Sso;

namespace ProfileSvr.Features.Auth;

public static class RefreshToken
{
    public record Request(string Token);

    private record Response(
        string AccessToken,
        string? RefreshToken,
        string? IdToken,
        string TokenType,
        int ExpiresIn);

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
                .WithSummary("Exchange a refresh token for a new access token")
                .WithTags("Auth");
    }

    private static async Task<IResult> Handle(
        Request request,
        ISsoClient sso,
        IValidator<Request> validator,
        CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid)
            return ApiResults.ValidationFailed(validation);

        try
        {
            var tokens = await sso.RefreshTokenAsync(request.Token, ct);
            return ApiResults.Ok(new Response(
                tokens.AccessToken, tokens.RefreshToken, tokens.IdToken,
                tokens.TokenType, tokens.ExpiresIn),
                "Token refreshed.");
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
    }
}
