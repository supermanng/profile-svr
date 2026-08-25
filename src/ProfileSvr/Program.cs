using FluentValidation;
using Microsoft.EntityFrameworkCore;
using ProfileSvr.Common;
using ProfileSvr.Common.Kyc;
using ProfileSvr.Common.MessageCentre;
using ProfileSvr.Common.Sso;
using ProfileSvr.Database;

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

// KYC provider: real HTTP client when configured, deterministic mock for local dev.
var kycBaseUrl = builder.Configuration["Kyc:BaseUrl"];
if (!string.IsNullOrWhiteSpace(kycBaseUrl))
{
    builder.Services.AddHttpClient<IKycClient, KycHttpClient>(client =>
    {
        client.BaseAddress = new Uri(kycBaseUrl);
        client.Timeout = TimeSpan.FromSeconds(15);
        var apiKey = builder.Configuration["Kyc:ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
    });
}
else
{
    builder.Services.AddSingleton<IKycClient, MockKycClient>();
}

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
    await context.Response.WriteAsJsonAsync(new ApiResponse<object>(
        false, null,
        status == StatusCodes.Status400BadRequest
            ? "The request body is malformed."
            : "An unexpected error occurred."));
}));
app.UseStatusCodePages(async statusContext =>
{
    var response = statusContext.HttpContext.Response;
    await response.WriteAsJsonAsync(new ApiResponse<object>(
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

app.MapGet("/health", () => ApiResults.Ok(new { status = "healthy" }))
    .WithTags("Health");

app.MapEndpoints();

app.Run();

// Exposes the implicit Program class to WebApplicationFactory in the test project.
public partial class Program;
