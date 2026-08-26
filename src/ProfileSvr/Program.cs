using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Common.Accounts;
using ProfileSvr.Common.Kyc;
using ProfileSvr.Common.MessageCentre;
using ProfileSvr.Common.Sso;
using ProfileSvr.Database;
using Refit;

var builder = WebApplication.CreateBuilder(args);

// Connection string resolution order:
//   1. env var PROFILESVR_DB (full MySQL connection string)
//   2. ConnectionStrings:ProfileDb from appsettings / user-secrets
var connectionString = Environment.GetEnvironmentVariable("PROFILESVR_DB");
if (string.IsNullOrWhiteSpace(connectionString))
    connectionString = builder.Configuration.GetConnectionString("ProfileDb");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException(
        "No database connection string configured. Set the PROFILESVR_DB environment variable " +
        "or ConnectionStrings:ProfileDb.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(
        connectionString,
        new MySqlServerVersion(new Version(8, 0, 36)),
        mysql => mysql.EnableRetryOnFailure(maxRetryCount: 3)));

// Message Centre: real HTTP client when configured, logging fallback for local dev.
var messageCentreBaseUrl = builder.Configuration["MessageCentre:BaseUrl"];
if (!string.IsNullOrWhiteSpace(messageCentreBaseUrl))
{
    builder.Services.AddHttpClient<IMessageCentre, MessageCentreClient>(client =>
    {
        client.BaseAddress = new Uri(messageCentreBaseUrl);
        client.Timeout = TimeSpan.FromSeconds(10);
        var bearerToken = builder.Configuration["MessageCentre:BearerToken"];
        if (!string.IsNullOrWhiteSpace(bearerToken))
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {bearerToken}");
    });
}
else
{
    builder.Services.AddSingleton<IMessageCentre, LoggingMessageCentre>();
}

// SSO (Auth Service) — used to verify the email is registered before creating a profile.
var ssoBaseUrl = builder.Configuration["Sso:BaseUrl"]
    ?? throw new InvalidOperationException("Sso:BaseUrl is not configured.");
builder.Services.AddHttpClient<ISsoClient, SsoClient>(client =>
{
    client.BaseAddress = new Uri(ssoBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
    var bearerToken = builder.Configuration["Sso:BearerToken"];
    if (!string.IsNullOrWhiteSpace(bearerToken))
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {bearerToken}");
});

// Malformed request bodies (bad JSON, e.g. a non-GUID deviceId) return a quiet 400
// instead of throwing a logged exception (the Development default).
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

// Bearer tokens issued by the SSO (validated via its OpenID discovery + JWKS).
builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = ssoBaseUrl;
        options.MapInboundClaims = false; // keep raw claim names (SourceId, email, sub)
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateAudience = false,
            ValidateIssuer = true,
            ValidateLifetime = true
        };
    });
builder.Services.AddAuthorization(options =>
{
    // onboarding tokens reach only the onboarding process; profile-active reaches everything else.
    options.AddPolicy(TokenTypes.OnboardingPolicy,
        policy => policy.RequireClaim(TokenTypes.ClaimName, TokenTypes.Onboarding));
    options.AddPolicy(TokenTypes.ProfileActivePolicy,
        policy => policy.RequireClaim(TokenTypes.ClaimName, TokenTypes.ProfileActive));
});
builder.Services.AddScoped<Microsoft.AspNetCore.Authentication.IClaimsTransformation, ProfileTypeClaimsTransformation>();

// KYC provider (Dojah gateway): Refit client when configured, deterministic mock for local dev.
var kycBaseUrl = builder.Configuration["Kyc:BaseUrl"];
if (!string.IsNullOrWhiteSpace(kycBaseUrl))
{
    builder.Services.AddRefitClient<IKycApi>()
        .ConfigureHttpClient(client =>
        {
            client.BaseAddress = new Uri(kycBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
            var apiKey = builder.Configuration["Kyc:ApiKey"];
            if (!string.IsNullOrWhiteSpace(apiKey))
                client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        });
    builder.Services.AddTransient<IKycClient, KycHttpClient>();
}
else
{
    builder.Services.AddSingleton<IKycClient, MockKycClient>();
}

// Face verification (AWS Rekognition liveness + compare): real when AWS keys are configured.
// The permissive mock is used when the keys are missing OR Aws:UseMockFaceVerification is
// true (appsettings, or env var Aws__UseMockFaceVerification) — handy for API testing
// without the mobile liveness SDK. Never enable the mock in a real environment.
var useMockFaceVerification =
    builder.Configuration.GetValue("Aws:UseMockFaceVerification", false) ||
    string.IsNullOrWhiteSpace(builder.Configuration["Aws:AccessKey"]);
if (useMockFaceVerification)
    builder.Services.AddSingleton<IFaceVerificationService, MockFaceVerificationService>();
else
    builder.Services.AddSingleton<IFaceVerificationService, AwsFaceVerificationService>();

// Core-banking accounts (NGN + CAD): OneCore Refit provider when configured, deterministic mock
// for local dev. Endpoints only see IAccountFacade — swap IAccountProvider to change banks.
var oneCoreBaseUrl = builder.Configuration["OneCore:BaseUrl"];
if (!string.IsNullOrWhiteSpace(oneCoreBaseUrl))
{
    builder.Services.AddRefitClient<IOneCoreTokenApi>()
        .ConfigureHttpClient(client =>
        {
            client.BaseAddress = new Uri(builder.Configuration["OneCore:SsoBaseUrl"]
                ?? throw new InvalidOperationException("OneCore:SsoBaseUrl is not configured."));
            client.Timeout = TimeSpan.FromSeconds(15);
        });
    builder.Services.AddSingleton<OneCoreTokenCache>();
    builder.Services.AddTransient<OneCoreAuthHandler>();
    builder.Services.AddRefitClient<IOneCoreApi>()
        .ConfigureHttpClient(client =>
        {
            client.BaseAddress = new Uri(oneCoreBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddHttpMessageHandler<OneCoreAuthHandler>();
    builder.Services.AddTransient<IAccountProvider, OneCoreAccountProvider>();
}
else
{
    builder.Services.AddSingleton<IAccountProvider, MockAccountProvider>();
}
builder.Services.AddScoped<IAccountFacade, AccountFacade>();

// Virtual (collection) accounts: VantPay Refit provider when configured, deterministic mock
// for local dev. Its bearer token comes from the VantPay SSO, not the OneCore one.
var digitVirtualBaseUrl = builder.Configuration["DigitVirtual:BaseUrl"];
if (!string.IsNullOrWhiteSpace(digitVirtualBaseUrl))
{
    builder.Services.AddRefitClient<ISsoTokenApi>()
        .ConfigureHttpClient(client =>
        {
            client.BaseAddress = new Uri(builder.Configuration["DigitVirtual:SsoBaseUrl"]
                ?? throw new InvalidOperationException("DigitVirtual:SsoBaseUrl is not configured."));
            client.Timeout = TimeSpan.FromSeconds(15);
        });
    builder.Services.AddSingleton<VirtualAccountTokenCache>();
    builder.Services.AddTransient<VirtualAccountAuthHandler>();
    builder.Services.AddRefitClient<IVirtualAccountApi>()
        .ConfigureHttpClient(client =>
        {
            client.BaseAddress = new Uri(digitVirtualBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddHttpMessageHandler<VirtualAccountAuthHandler>();
    builder.Services.AddTransient<IVirtualAccountProvider, VantPayVirtualAccountProvider>();
}
else
{
    builder.Services.AddSingleton<IVirtualAccountProvider, MockVirtualAccountProvider>();
}

// Incoming virtual-account credits: stored by the webhook, settled into the CBA naira
// account by the background job (VirtualAccountCredit:Enabled=false turns the job off).
builder.Services.AddHttpClient();
builder.Services.AddScoped<ICbaCreditPoster, CbaCreditPoster>();
builder.Services.AddScoped<IVirtualAccountCreditProcessor, VirtualAccountCreditProcessor>();
builder.Services.AddSingleton<ProfileSvr.Jobs.JobHeartbeat>();
if (builder.Configuration.GetValue("VirtualAccountCredit:Enabled", true))
    builder.Services.AddHostedService<ProfileSvr.Jobs.PostCbaCreditsJob>();

builder.Services.AddHttpContextAccessor();

// Enums bind from strings in request/response JSON (e.g. "section": "Phone").
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddOpenApi(options =>
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info = new()
        {
            Title = "Profile Service",
            Version = "v1",
            Description =
                "Profile microservice: OTP-verified onboarding (SSO user + profile creation), " +
                "device registration, and email/WhatsApp confirmation via the Message Centre."
        };
        return Task.CompletedTask;
    }));
builder.Services.AddProblemDetails();

var app = builder.Build();

if (useMockFaceVerification)
    app.Logger.LogWarning(
        "[DEV ONLY] Face verification is MOCKED (Aws:UseMockFaceVerification / missing AWS keys) — " +
        "every liveness session and face comparison passes.");

ActivityLog.HttpContextAccessor = app.Services.GetRequiredService<IHttpContextAccessor>();

// Outermost so every API request/response (including error envelopes) is encrypted when enabled.
app.UseMiddleware<PayloadEncryptionMiddleware>();

// Apply pending migrations on startup so the schema stays in sync with the code.
// (Migrations are MySQL-specific; other providers — e.g. SQLite in tests — get EnsureCreated.)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.ProviderName?.Contains("MySql", StringComparison.OrdinalIgnoreCase) == true)
        db.Database.Migrate();
    else
        db.Database.EnsureCreated();
}

// Unhandled exceptions and body-less error statuses all use the same ApiResponse envelope.
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var ex = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var status = ex is BadHttpRequestException bad
        ? bad.StatusCode
        : StatusCodes.Status500InternalServerError;
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new ProfileSvr.Common.ApiResponse<object>(
        false, null,
        status == StatusCodes.Status400BadRequest
            ? "The request body is malformed."
            : "An unexpected error occurred."));
}));
app.UseStatusCodePages(async statusContext =>
{
    var response = statusContext.HttpContext.Response;
    await response.WriteAsJsonAsync(new ProfileSvr.Common.ApiResponse<object>(
        false, null,
        Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(response.StatusCode)));
});
app.MapOpenApi();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "Profile Service v1");
    options.DocumentTitle = "Profile Service — Swagger";
});

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", (ProfileSvr.Jobs.JobHeartbeat heartbeat, IConfiguration config) =>
{
    // The credit-posting job's heartbeat degrades health when the job stalls.
    var jobEnabled = config.GetValue("VirtualAccountCredit:Enabled", true);
    var intervalSeconds = config.GetValue("VirtualAccountCredit:IntervalSeconds", 2);
    var jobHealthy = !jobEnabled || heartbeat.IsHealthy(intervalSeconds);
    return ApiResults.Ok(new
    {
        status = jobHealthy ? "healthy" : "degraded",
        creditJob = new
        {
            name = heartbeat.JobName,
            enabled = jobEnabled,
            lastTickUtc = heartbeat.LastTickUtc,
            ticks = heartbeat.TickCount,
            healthy = jobHealthy
        }
    });
}).WithTags("Health");

app.MapEndpoints();

app.Run();

// Exposes the implicit Program class to WebApplicationFactory in the test project.
public partial class Program;
