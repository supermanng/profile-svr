using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProfileSvr.Common.Accounts;
using ProfileSvr.Common.Kyc;
using ProfileSvr.Common.MessageCentre;
using ProfileSvr.Common.Sso;
using ProfileSvr.Database;

namespace ProfileSvr.Tests.Infrastructure;

/// <summary>
/// Boots the whole app with a shared in-memory SQLite database, fake SSO, Message Centre
/// (which captures OTP codes), KYC/face-verification/account-provider fakes, and a
/// header-driven test auth scheme.
/// </summary>
public class TestAppFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection;

    public FakeMessageCentre MessageCentre { get; } = new();
    public FakeSsoClient Sso { get; } = new();
    public FakeFaceVerification Faces { get; } = new();
    public FakeAccountProvider Accounts { get; } = new();
    public FakeVirtualAccountProvider VirtualAccounts { get; } = new();
    public FakeCbaCreditPoster CbaPoster { get; } = new();

    public TestAppFactory()
    {
        Environment.SetEnvironmentVariable("PROFILESVR_DB",
            "Server=unused;Database=unused;User=unused;Password=unused");
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        // Tests drive the credit processor directly instead of racing the background job.
        builder.UseSetting("VirtualAccountCredit:Enabled", "false");
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

            // Deterministic KYC data regardless of the configured provider URL, plus
            // controllable face-verification and account-provider fakes.
            services.RemoveAll<IKycClient>();
            services.AddSingleton<IKycClient, MockKycClient>();
            services.RemoveAll<IFaceVerificationService>();
            services.AddSingleton<IFaceVerificationService>(Faces);
            services.RemoveAll<IAccountProvider>();
            services.AddSingleton<IAccountProvider>(Accounts);
            services.RemoveAll<IVirtualAccountProvider>();
            services.AddSingleton<IVirtualAccountProvider>(VirtualAccounts);
            services.RemoveAll<ICbaCreditPoster>();
            services.AddSingleton<ICbaCreditPoster>(CbaPoster);

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

    /// <summary>When set, RefreshTokenAsync returns this as the access token (e.g. a test JWT).</summary>
    public string? RefreshAccessTokenOverride { get; set; }

    public Task<SsoTokens> RefreshTokenAsync(string refreshToken, CancellationToken ct) =>
        refreshToken == "refresh-token"
            ? Task.FromResult(new SsoTokens(
                RefreshAccessTokenOverride ?? "access-token-2", "refresh-token-2", null, "Bearer", 3600))
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

/// <summary>Controllable face-verification fake: liveness confidence and match outcome are knobs.</summary>
public class FakeFaceVerification : IFaceVerificationService
{
    public double NextLivenessConfidence { get; set; } = 99.0;
    public FaceComparison NextComparison { get; set; } = new(true, 98.5);
    public List<string> CreatedSessions { get; } = [];

    public Task<LivenessSession> CreateLivenessSessionAsync(CancellationToken ct)
    {
        var sessionId = $"session-{Guid.NewGuid():N}";
        CreatedSessions.Add(sessionId);
        return Task.FromResult(new LivenessSession(sessionId, "fake-auth-token"));
    }

    public Task<LivenessResult> GetLivenessResultAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult(new LivenessResult(
            sessionId, NextLivenessConfidence, NextLivenessConfidence >= 75.0, MockKycClient.TinyPngBase64));

    public Task<FaceComparison> CompareFacesAsync(
        string sourceImageBase64, string targetImageBase64, CancellationToken ct) =>
        Task.FromResult(NextComparison);
}

/// <summary>
/// Controllable core-banking fake: FailAll simulates a provider outage so tests can
/// exercise the login/refresh self-heal.
/// </summary>
public class FakeAccountProvider : IAccountProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<AccountDetail>> _accounts = [];
    private long _accountSeq = 3000000000;

    public bool FailAll { get; set; }

    public Task<string?> GetOrCreateCustomerAsync(AccountHolder holder, CancellationToken ct)
    {
        ThrowIfDown();
        return Task.FromResult<string?>("CIF-" + holder.Email);
    }

    public Task<IReadOnlyList<AccountDetail>> GetAccountsAsync(string customerId, CancellationToken ct)
    {
        ThrowIfDown();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<AccountDetail>>(
                _accounts.TryGetValue(customerId, out var list) ? [.. list] : []);
        }
    }

    public Task<string?> CreateAccountAsync(string customerId, string currency, CancellationToken ct)
    {
        ThrowIfDown();
        lock (_gate)
        {
            var list = _accounts.TryGetValue(customerId, out var existing)
                ? existing
                : _accounts[customerId] = [];
            var number = Interlocked.Increment(ref _accountSeq).ToString();
            list.Add(new AccountDetail($"Test {currency} Account", number, currency, 0m));
            return Task.FromResult<string?>(number);
        }
    }

    public Task<IReadOnlyList<CurrencyRate>> GetRatesAsync(CancellationToken ct)
    {
        ThrowIfDown();
        var now = DateTime.UtcNow;
        return Task.FromResult<IReadOnlyList<CurrencyRate>>(
        [
            new CurrencyRate("CAD", "NGN", "FX", 1080m, 1050m, now, now),
            new CurrencyRate("NGN", "CAD", "FX", 0.00095m, 0.00090m, now, now)
        ]);
    }

    private void ThrowIfDown()
    {
        if (FailAll)
            throw new AccountProviderException("The provider is down (test).");
    }
}

/// <summary>Controllable virtual-account fake: FailAll simulates a provider outage.</summary>
public class FakeVirtualAccountProvider : IVirtualAccountProvider
{
    private long _accountSeq = 8800000000;

    public bool FailAll { get; set; }

    public Task<VirtualAccountInfo?> CreateVirtualAccountAsync(string? bvn, string? nin, CancellationToken ct)
    {
        if (FailAll)
            throw new AccountProviderException("The provider is down (test).");
        return Task.FromResult<VirtualAccountInfo?>(new VirtualAccountInfo(
            Interlocked.Increment(ref _accountSeq).ToString(),
            "Test Virtual Account",
            "Test Microfinance Bank"));
    }
}

/// <summary>Records CBA credit postings; NextFailure makes the next post(s) throw.</summary>
public class FakeCbaCreditPoster : ICbaCreditPoster
{
    public record PostedCredit(
        string Reference, string CbaAccount, decimal Amount,
        decimal Charge, decimal ProviderCharge, string? Sender);

    public List<PostedCredit> Posts { get; } = [];
    public Exception? NextFailure { get; set; }

    public Task<string> PostCreditAsync(
        string reference, string cbaAccountNumber, decimal amount,
        decimal charge, decimal providerCharge, string? sender, CancellationToken ct)
    {
        if (NextFailure is not null)
            throw NextFailure;
        Posts.Add(new PostedCredit(reference, cbaAccountNumber, amount, charge, providerCharge, sender));
        return Task.FromResult("{\"success\":true}");
    }
}
