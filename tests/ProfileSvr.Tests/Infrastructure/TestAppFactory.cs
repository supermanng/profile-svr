using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProfileSvr.Common.MessageCentre;
using ProfileSvr.Common.Sso;
using ProfileSvr.Database;

namespace ProfileSvr.Tests.Infrastructure;

/// <summary>
/// Boots the whole app with a shared in-memory SQLite database, fake SSO and
/// Message Centre (which captures OTP codes), and a header-driven test auth scheme.
/// </summary>
public class TestAppFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection;

    public FakeMessageCentre MessageCentre { get; } = new();
    public FakeSsoClient Sso { get; } = new();

    public TestAppFactory()
    {
        // Config via env vars so the app starts without needing appsettings.json at the content root
        // (see CreateHost — the content root is forced to the test bin dir, which has no appsettings).
        Environment.SetEnvironmentVariable("PROFILESVR_DB",
            "Server=unused;Database=unused;User=unused;Password=unused");
        Environment.SetEnvironmentVariable("Sso__BaseUrl", "https://sso.test");
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // The MvcTesting manifest bakes the build-time source path as the content root; on a machine
        // where that path is absent the PhysicalFileProvider throws. Force a root that always exists.
        builder.UseContentRoot(AppContext.BaseDirectory);
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseContentRoot(AppContext.BaseDirectory);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));

            services.RemoveAll<IMessageCentre>();
            services.AddSingleton<IMessageCentre>(MessageCentre);
            services.RemoveAll<ISsoClient>();
            services.AddSingleton<ISsoClient>(Sso);

            services.AddAuthentication(TestAuthHandler.Scheme)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, null);
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.Scheme;
                options.DefaultChallengeScheme = TestAuthHandler.Scheme;
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _connection.Dispose();
    }
}

/// <summary>
/// Authenticates requests carrying an X-Test-User header (the email);
/// optional X-Test-SourceId supplies the SourceId claim.
/// </summary>
public class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-User", out var email))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim>
        {
            new("email", email.ToString()),
            new("name", email.ToString())
        };
        if (Request.Headers.TryGetValue("X-Test-SourceId", out var sourceId))
            claims.Add(new Claim("SourceId", sourceId.ToString()));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme)));
    }
}

public class FakeMessageCentre : IMessageCentre
{
    public record SentMessage(string Channel, string To, string Code);

    public List<SentMessage> Sent { get; } = [];

    public string? LastCodeFor(string to) =>
        Sent.LastOrDefault(m => m.To == to)?.Code;

    public Task SendEmailOtpAsync(string emailAddress, string code, string heading, string intro, CancellationToken ct)
    {
        Sent.Add(new SentMessage("email", emailAddress, code));
        return Task.CompletedTask;
    }

    public ProfileSvr.Domain.OtpChannel PhoneOtpChannel => ProfileSvr.Domain.OtpChannel.WhatsApp;

    public Task SendPhoneOtpAsync(string phoneNumber, string code, CancellationToken ct)
    {
        Sent.Add(new SentMessage("whatsapp", phoneNumber, code));
        return Task.CompletedTask;
    }
}

public class FakeSsoClient : ISsoClient
{
    public const string CorrectPassword = "Correct-Pass1!";

    public HashSet<string> ExistingEmails { get; } = [];
    public List<(string Username, string Email, string SourceId)> CreatedUsers { get; } = [];

    public Task<bool> EmailExistsAsync(string email, CancellationToken ct) =>
        Task.FromResult(ExistingEmails.Contains(email));

    public Task CreateUserAsync(string username, string password, string email, string sourceId, CancellationToken ct)
    {
        CreatedUsers.Add((username, email, sourceId));
        ExistingEmails.Add(email);
        return Task.CompletedTask;
    }

    public Task<SsoTokens> PasswordLoginAsync(string username, string password, CancellationToken ct) =>
        password == CorrectPassword
            ? Task.FromResult(new SsoTokens("access-token", "refresh-token", "id-token", "Bearer", 3600))
            : throw new SsoException("Invalid username or password.");

    public Task<SsoTokens> RefreshTokenAsync(string refreshToken, CancellationToken ct) =>
        refreshToken == "refresh-token"
            ? Task.FromResult(new SsoTokens("access-token-2", "refresh-token-2", null, "Bearer", 3600))
            : throw new SsoException("The refresh token is invalid or expired.");

    public Task<string> InitiatePasswordResetAsync(string username, CancellationToken ct) =>
        Task.FromResult("password-reset-token");

    public Task ResetPasswordAsync(string username, string passwordResetToken, string newPassword, CancellationToken ct) =>
        passwordResetToken == "password-reset-token"
            ? Task.CompletedTask
            : throw new SsoException("The SSO rejected the password reset.");

    public Task ChangePasswordAsync(string username, string currentPassword, string newPassword, CancellationToken ct) =>
        currentPassword == CorrectPassword
            ? Task.CompletedTask
            : throw new SsoException("The current password is incorrect.");

    public Dictionary<string, string> TypeClaims { get; } = [];

    public Task SetTypeClaimAsync(string username, string value, CancellationToken ct)
    {
        TypeClaims[username] = value;
        return Task.CompletedTask;
    }
}
