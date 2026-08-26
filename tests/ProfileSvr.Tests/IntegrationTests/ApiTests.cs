using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProfileSvr.Tests.Infrastructure;

namespace ProfileSvr.Tests.IntegrationTests;

public class ApiTests(TestAppFactory factory) : IClassFixture<TestAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    // ---------- helpers ----------

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<JsonElement> Post(string url, object body, string? asUser = null,
        HttpStatusCode? expect = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (asUser is not null)
            request.Headers.Add("X-Test-User", asUser);
        var response = await _client.SendAsync(request);
        if (expect is not null)
            Assert.Equal(expect, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<JsonElement> Get(string url, string? asUser = null, HttpStatusCode? expect = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (asUser is not null)
            request.Headers.Add("X-Test-User", asUser);
        var response = await _client.SendAsync(request);
        if (expect is not null)
            Assert.Equal(expect, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<JsonElement> PostWithSource(string url, object body, string asUser, Guid sourceId,
        HttpStatusCode? expect = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Test-User", asUser);
        request.Headers.Add("X-Test-SourceId", sourceId.ToString());
        var response = await _client.SendAsync(request);
        if (expect is not null)
            Assert.Equal(expect, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static object NewDevice(string id) => new
    {
        deviceIdentifier = id,
        name = "Test Device",
        platform = "test",
        osName = "TestOS",
        osVersion = "1.0",
        manufacturer = "Acme",
        model = "T1000",
        appVersion = "1.0.0"
    };

    /// <summary>Runs the full email onboarding for a fresh user; returns (email, deviceId, profileId).</summary>
    private async Task<(string Email, Guid DeviceId, Guid ProfileId)> OnboardAsync(string tag)
    {
        var email = $"{tag}@example.com";

        var initiate = await Post("/api/onboarding/initiate",
            new { emailAddress = email, device = NewDevice($"device-{tag}") },
            expect: HttpStatusCode.Accepted);
        var deviceId = initiate.GetProperty("data").GetProperty("deviceId").GetGuid();
        var retrievalCode = initiate.GetProperty("data").GetProperty("retrievalCode").GetString()!;
        var otp = factory.MessageCentre.LastCodeFor(email)!;

        var verify = await Post("/api/onboarding/verify-auth",
            new { emailAddress = email, deviceId, retrievalCode, otp, password = FakeSsoClient.CorrectPassword },
            expect: HttpStatusCode.Created);
        var profileId = verify.GetProperty("data").GetProperty("profileId").GetGuid();

        return (email, deviceId, profileId);
    }

    private static int _phoneCounter = 10000000;

    /// <summary>Full onboarding to status Active (phone verified + profile created).</summary>
    private async Task<(string Email, Guid DeviceId, Guid ProfileId)> OnboardActiveAsync(string tag)
    {
        var (email, deviceId, profileId) = await OnboardAsync(tag);
        var phone = "+2348" + Interlocked.Increment(ref _phoneCounter);

        var initiate = await Post("/api/onboarding/initiate-phone",
            new { phoneNumber = phone, deviceId }, asUser: email, expect: HttpStatusCode.Accepted);
        var retrievalCode = initiate.GetProperty("data").GetProperty("retrievalCode").GetString()!;
        var otp = factory.MessageCentre.LastCodeFor(phone)!;
        await Post("/api/otp/verify",
            new { deviceId, retrievalCode, otp, section = "Phone" }, asUser: email,
            expect: HttpStatusCode.OK);
        await Post("/api/onboarding/create-profile",
            new { deviceId, firstName = "Test", lastName = "User", dateOfBirth = "1990-01-01" },
            asUser: email, expect: HttpStatusCode.OK);

        return (email, deviceId, profileId);
    }

    // ---------- health & envelope ----------

    [Fact]
    public async Task Health_ReturnsSuccessEnvelope()
    {
        var body = await Get("/health", expect: HttpStatusCode.OK);
        Assert.True(body.GetProperty("isSuccess").GetBoolean());
        Assert.Equal("healthy", body.GetProperty("data").GetProperty("status").GetString());
    }

    // ---------- onboarding: email leg ----------

    [Fact]
    public async Task Onboarding_FullEmailFlow_CreatesSsoUserAndProfile()
    {
        var (email, deviceId, profileId) = await OnboardAsync("flow1");

        Assert.Contains(factory.Sso.CreatedUsers, u => u.Email == email && u.SourceId == profileId.ToString());

        // verify-auth returns onboarding-typed tokens so the flow continues without login.
        var initiate2 = await Post("/api/onboarding/initiate",
            new { emailAddress = "flow1b@example.com", device = NewDevice("device-flow1b") },
            expect: HttpStatusCode.Accepted);
        var verify2 = await Post("/api/onboarding/verify-auth",
            new
            {
                emailAddress = "flow1b@example.com",
                deviceId = initiate2.GetProperty("data").GetProperty("deviceId").GetGuid(),
                retrievalCode = initiate2.GetProperty("data").GetProperty("retrievalCode").GetString(),
                otp = factory.MessageCentre.LastCodeFor("flow1b@example.com"),
                password = FakeSsoClient.CorrectPassword
            },
            expect: HttpStatusCode.Created);
        Assert.Equal("onboarding", verify2.GetProperty("data").GetProperty("type").GetString());
        Assert.Equal("access-token",
            verify2.GetProperty("data").GetProperty("tokens").GetProperty("accessToken").GetString());
        Assert.Equal("onboarding", factory.Sso.TypeClaims["flow1b@example.com"]);

        var profile = await Get($"/api/profiles/{profileId}", expect: HttpStatusCode.OK);
        var data = profile.GetProperty("data");
        Assert.True(data.GetProperty("emailConfirmed").GetBoolean());
        Assert.Equal(deviceId, data.GetProperty("activeDeviceId").GetGuid());
    }

    [Fact]
    public async Task Onboarding_EmailAlreadyOnSso_Conflicts()
    {
        factory.Sso.ExistingEmails.Add("taken@example.com");
        var body = await Post("/api/onboarding/initiate",
            new { emailAddress = "taken@example.com", device = NewDevice("device-taken") },
            expect: HttpStatusCode.Conflict);
        Assert.False(body.GetProperty("isSuccess").GetBoolean());
    }

    [Fact]
    public async Task Onboarding_ImmediateResend_HitsCooldown()
    {
        await Post("/api/onboarding/initiate",
            new { emailAddress = "cooldown@example.com", device = NewDevice("device-cooldown") },
            expect: HttpStatusCode.Accepted);
        await Post("/api/onboarding/initiate",
            new { emailAddress = "cooldown@example.com", device = NewDevice("device-cooldown") },
            expect: HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Onboarding_WrongOtp_IsRejected()
    {
        var email = "wrongotp@example.com";
        var initiate = await Post("/api/onboarding/initiate",
            new { emailAddress = email, device = NewDevice("device-wrongotp") },
            expect: HttpStatusCode.Accepted);
        var deviceId = initiate.GetProperty("data").GetProperty("deviceId").GetGuid();
        var retrievalCode = initiate.GetProperty("data").GetProperty("retrievalCode").GetString()!;
        var wrongOtp = factory.MessageCentre.LastCodeFor(email) == "000000" ? "000001" : "000000";

        var body = await Post("/api/onboarding/verify-auth",
            new { emailAddress = email, deviceId, retrievalCode, otp = wrongOtp, password = FakeSsoClient.CorrectPassword },
            expect: HttpStatusCode.BadRequest);
        Assert.Equal("The code is incorrect.", body.GetProperty("message").GetString());
    }

    // ---------- auth ----------

    [Fact]
    public async Task Login_ReturnsTokensAndProfileDto()
    {
        var (email, deviceId, profileId) = await OnboardAsync("login1");

        var body = await Post("/api/auth/login",
            new { username = email, password = FakeSsoClient.CorrectPassword, deviceId },
            expect: HttpStatusCode.OK);
        var data = body.GetProperty("data");
        Assert.Equal("access-token", data.GetProperty("accessToken").GetString());
        Assert.Equal("Existing", data.GetProperty("deviceStatus").GetString()); // bound at onboarding
        Assert.Equal(profileId, data.GetProperty("profile").GetProperty("profileId").GetGuid());
        Assert.Equal(deviceId, data.GetProperty("profile").GetProperty("activeDeviceId").GetGuid());
        Assert.True(data.GetProperty("profile").GetProperty("emailConfirmed").GetBoolean());
    }

    [Fact]
    public async Task Login_WrongPassword_Is401()
    {
        var (email, deviceId, _) = await OnboardAsync("login2");
        await Post("/api/auth/login",
            new { username = email, password = "Wrong-Pass1!", deviceId },
            expect: HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_UnknownDevice_Is422()
    {
        await Post("/api/auth/login",
            new { username = "whoever@example.com", password = FakeSsoClient.CorrectPassword, deviceId = Guid.NewGuid() },
            expect: HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Login_FromDifferentDevice_Is403()
    {
        var (email, _, _) = await OnboardAsync("login3");

        // Register a second device via another user's onboarding, then try to log in with it.
        var (_, otherDevice, _) = await OnboardAsync("login3b");
        await Post("/api/auth/login",
            new { username = email, password = FakeSsoClient.CorrectPassword, deviceId = otherDevice },
            expect: HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Me_ReturnsProfile_AndRejectsUnknownUser()
    {
        var (email, deviceId, profileId) = await OnboardAsync("me1");

        var body = await Get("/api/auth/me", asUser: email, expect: HttpStatusCode.OK);
        Assert.Equal(profileId, body.GetProperty("data").GetProperty("profileId").GetGuid());
        Assert.Equal(deviceId, body.GetProperty("data").GetProperty("activeDeviceId").GetGuid());
        Assert.True(body.GetProperty("data").GetProperty("deviceRecentlyLinked").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("data").GetProperty("activeDeviceLinkedAtUtc").ValueKind);

        await Get("/api/auth/me", asUser: "nobody@example.com", expect: HttpStatusCode.Forbidden);
        await Get("/api/auth/me", expect: HttpStatusCode.Unauthorized);
    }

    // ---------- phone leg + unified OTP verify ----------

    [Fact]
    public async Task PhoneFlow_InitiateAndVerifySection_ConfirmsPhone()
    {
        var (email, deviceId, _) = await OnboardAsync("phone1");
        var phone = "+2348090000001";

        var initiate = await Post("/api/onboarding/initiate-phone",
            new { phoneNumber = phone, deviceId }, asUser: email,
            expect: HttpStatusCode.Accepted);
        var retrievalCode = initiate.GetProperty("data").GetProperty("retrievalCode").GetString()!;
        var otp = factory.MessageCentre.LastCodeFor(phone)!;
        Assert.Contains(factory.MessageCentre.Sent, m => m.To == phone && m.Channel == "whatsapp");

        var verify = await Post("/api/otp/verify",
            new { deviceId, retrievalCode, otp, section = "Phone" }, asUser: email,
            expect: HttpStatusCode.OK);
        Assert.True(verify.GetProperty("data").GetProperty("phoneNumberConfirmed").GetBoolean());
    }

    [Fact]
    public async Task OtpVerify_LocksAfterMaxAttempts()
    {
        var (email, deviceId, _) = await OnboardAsync("lockout1");
        var phone = "+2348090000002";

        var initiate = await Post("/api/onboarding/initiate-phone",
            new { phoneNumber = phone, deviceId }, asUser: email,
            expect: HttpStatusCode.Accepted);
        var retrievalCode = initiate.GetProperty("data").GetProperty("retrievalCode").GetString()!;
        var correctOtp = factory.MessageCentre.LastCodeFor(phone)!;
        var wrongOtp = correctOtp == "111111" ? "222222" : "111111";

        for (var i = 0; i < 5; i++)
            await Post("/api/otp/verify",
                new { deviceId, retrievalCode, otp = wrongOtp, section = "Phone" }, asUser: email,
                expect: HttpStatusCode.BadRequest);

        // Sixth attempt — even with the right code — is locked out.
        await Post("/api/otp/verify",
            new { deviceId, retrievalCode, otp = correctOtp, section = "Phone" }, asUser: email,
            expect: HttpStatusCode.TooManyRequests);
    }

    // ---------- KYC path (Dojah lookup + AWS liveness) ----------

    /// <summary>Runs initiate-kyc, verifies the phone OTP, then complete-kyc; returns the complete-kyc data.</summary>
    private async Task<JsonElement> CompleteKycAsync(string email, Guid deviceId, string bvn)
    {
        var initiate = await Post("/api/onboarding/initiate-kyc",
            new { deviceId, bvn }, asUser: email, expect: HttpStatusCode.OK);
        var data = initiate.GetProperty("data");
        var sessionId = data.GetProperty("liveness").GetProperty("sessionId").GetString()!;

        // The OTP went to the phone on the KYC record (the mock derives it from the BVN).
        var kycPhone = "+234801" + bvn[^7..];
        var retrievalCode = data.GetProperty("otp").GetProperty("retrievalCode").GetString()!;
        var otp = factory.MessageCentre.LastCodeFor(kycPhone)!;
        await Post("/api/otp/verify",
            new { deviceId, retrievalCode, otp, section = "Phone" }, asUser: email,
            expect: HttpStatusCode.OK);

        var complete = await Post("/api/onboarding/complete-kyc",
            new { deviceId, sessionId }, asUser: email, expect: HttpStatusCode.OK);
        return complete.GetProperty("data");
    }

    /// <summary>Full onboarding to status Active via the KYC path (BVN verified, tier 1).</summary>
    private async Task<(string Email, Guid DeviceId, Guid ProfileId, JsonElement CreateData)> OnboardKycActiveAsync(
        string tag, string bvn)
    {
        var (email, deviceId, profileId) = await OnboardAsync(tag);
        await CompleteKycAsync(email, deviceId, bvn);
        var create = await Post("/api/onboarding/create-profile",
            new { deviceId, firstName = "Test", lastName = "User", dateOfBirth = "1990-01-01" },
            asUser: email, expect: HttpStatusCode.OK);
        return (email, deviceId, profileId, create.GetProperty("data"));
    }

    [Fact]
    public async Task KycFlow_ReturnsIdentityAndLivenessSession_AndVerifiesToTierOne()
    {
        var (email, deviceId, profileId) = await OnboardAsync("kyc1");

        var initiate = await Post("/api/onboarding/initiate-kyc",
            new { deviceId, bvn = "22233344455" }, asUser: email,
            expect: HttpStatusCode.OK);
        var data = initiate.GetProperty("data");
        Assert.Equal("bvn", data.GetProperty("idType").GetString());
        Assert.Equal("Pending", data.GetProperty("kycStatus").GetString());

        // The Dojah identity details come back for the client to confirm, phone masked.
        var identity = data.GetProperty("identity");
        Assert.Equal("Adaeze", identity.GetProperty("firstName").GetString());
        Assert.Equal("Okafor", identity.GetProperty("lastName").GetString());
        Assert.StartsWith("*", identity.GetProperty("maskedPhoneNumber").GetString());
        Assert.EndsWith("4455", identity.GetProperty("maskedPhoneNumber").GetString());
        Assert.False(string.IsNullOrEmpty(identity.GetProperty("image").GetString()));

        // Plus the AWS liveness session for the client SDK.
        var liveness = data.GetProperty("liveness");
        var sessionId = liveness.GetProperty("sessionId").GetString()!;
        Assert.False(string.IsNullOrEmpty(sessionId));
        Assert.False(string.IsNullOrEmpty(liveness.GetProperty("authToken").GetString()));

        // And an OTP to the phone on the record — verifying it confirms the phone.
        var otpInfo = data.GetProperty("otp");
        Assert.Equal("WhatsApp", otpInfo.GetProperty("channel").GetString());
        var retrievalCode = otpInfo.GetProperty("retrievalCode").GetString()!;
        var kycPhone = "+234801" + "22233344455"[^7..];
        var otp = factory.MessageCentre.LastCodeFor(kycPhone)!;
        var verify = await Post("/api/otp/verify",
            new { deviceId, retrievalCode, otp, section = "Phone" }, asUser: email,
            expect: HttpStatusCode.OK);
        Assert.True(verify.GetProperty("data").GetProperty("phoneNumberConfirmed").GetBoolean());
        Assert.False(verify.GetProperty("data").GetProperty("bvnIsVerified").GetBoolean()); // OTP alone ≠ KYC

        // Completing runs the liveness check + face comparison and promotes to tier 1.
        var complete = await Post("/api/onboarding/complete-kyc",
            new { deviceId, sessionId }, asUser: email, expect: HttpStatusCode.OK);
        var completed = complete.GetProperty("data");
        Assert.True(completed.GetProperty("isLive").GetBoolean());
        Assert.True(completed.GetProperty("faceMatch").GetBoolean());
        Assert.True(completed.GetProperty("bvnIsVerified").GetBoolean());
        Assert.True(completed.GetProperty("phoneNumberConfirmed").GetBoolean());
        Assert.Equal(1, completed.GetProperty("tier").GetInt32());
        Assert.Equal("Approved", completed.GetProperty("kycStatus").GetString());

        var me = await Get("/api/auth/me", asUser: email, expect: HttpStatusCode.OK);
        Assert.Equal("Adaeze", me.GetProperty("data").GetProperty("firstName").GetString());
        Assert.True(me.GetProperty("data").GetProperty("phoneNumberConfirmed").GetBoolean());
        Assert.Equal(profileId, me.GetProperty("data").GetProperty("profileId").GetGuid());
    }

    [Fact]
    public async Task Kyc_FaceMatchAlone_DoesNotConfirmPhone_AndBlocksCreateProfile()
    {
        var (email, deviceId, _) = await OnboardAsync("kyc6");

        var initiate = await Post("/api/onboarding/initiate-kyc",
            new { deviceId, bvn = "22233344588" }, asUser: email, expect: HttpStatusCode.OK);
        var data = initiate.GetProperty("data");
        var sessionId = data.GetProperty("liveness").GetProperty("sessionId").GetString()!;

        // Completing KYC without the OTP verifies the identity but not the phone.
        var complete = await Post("/api/onboarding/complete-kyc",
            new { deviceId, sessionId }, asUser: email, expect: HttpStatusCode.OK);
        Assert.True(complete.GetProperty("data").GetProperty("bvnIsVerified").GetBoolean());
        Assert.False(complete.GetProperty("data").GetProperty("phoneNumberConfirmed").GetBoolean());

        // So the final step is still blocked...
        await Post("/api/onboarding/create-profile",
            new { deviceId, firstName = "Test", lastName = "User", dateOfBirth = "1990-01-01" },
            asUser: email, expect: HttpStatusCode.UnprocessableEntity);

        // ...until the OTP sent to the phone on the BVN record is verified.
        var retrievalCode = data.GetProperty("otp").GetProperty("retrievalCode").GetString()!;
        var otp = factory.MessageCentre.LastCodeFor("+234801" + "22233344588"[^7..])!;
        await Post("/api/otp/verify",
            new { deviceId, retrievalCode, otp, section = "Phone" }, asUser: email,
            expect: HttpStatusCode.OK);
        await Post("/api/onboarding/create-profile",
            new { deviceId, firstName = "Test", lastName = "User", dateOfBirth = "1990-01-01" },
            asUser: email, expect: HttpStatusCode.OK);
    }

    [Fact]
    public async Task Kyc_UnknownBvn_Is422_AndBothIdsRejected()
    {
        var (email, deviceId, _) = await OnboardAsync("kyc2");

        await Post("/api/onboarding/initiate-kyc",
            new { deviceId, bvn = "22233344400" }, asUser: email, // mock: ends 00 = not found
            expect: HttpStatusCode.UnprocessableEntity);

        await Post("/api/onboarding/initiate-kyc",
            new { deviceId, bvn = "22233344455", nin = "12345678901" }, asUser: email,
            expect: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Kyc_LivenessFailure_IsSoftFail_WithStatusLivenessFailed()
    {
        var (email, deviceId, _) = await OnboardAsync("kyc3");
        factory.Faces.NextLivenessConfidence = 40; // below the 75 threshold
        try
        {
            var data = await CompleteKycAsync(email, deviceId, "22233344466");
            Assert.False(data.GetProperty("isLive").GetBoolean());
            Assert.False(data.GetProperty("faceMatch").GetBoolean());
            Assert.False(data.GetProperty("bvnIsVerified").GetBoolean());
            Assert.Equal(0, data.GetProperty("tier").GetInt32());
            Assert.Equal("LivenessFailed", data.GetProperty("kycStatus").GetString());

            var me = await Get("/api/auth/me", asUser: email, expect: HttpStatusCode.OK);
            Assert.Equal("LivenessFailed", me.GetProperty("data").GetProperty("kycStatus").GetString());
        }
        finally
        {
            factory.Faces.NextLivenessConfidence = 99.0;
        }
    }

    [Fact]
    public async Task Kyc_FaceMismatch_IsSoftFail_WithStatusFaceMismatch()
    {
        var (email, deviceId, _) = await OnboardAsync("kyc4");
        factory.Faces.NextComparison = new ProfileSvr.Common.Kyc.FaceComparison(false, 12.5);
        try
        {
            var data = await CompleteKycAsync(email, deviceId, "22233344477");
            Assert.True(data.GetProperty("isLive").GetBoolean());
            Assert.False(data.GetProperty("faceMatch").GetBoolean());
            Assert.False(data.GetProperty("bvnIsVerified").GetBoolean());
            Assert.Equal("FaceMismatch", data.GetProperty("kycStatus").GetString());
        }
        finally
        {
            factory.Faces.NextComparison = new ProfileSvr.Common.Kyc.FaceComparison(true, 98.5);
        }
    }

    [Fact]
    public async Task PhoneFirstJourney_AccountsAreGeneratedAtCompleteKyc()
    {
        // The app's main journey: email → phone OTP → create-profile (no accounts yet) → KYC,
        // which verifies AND generates the accounts (the provider needs the verified BVN/NIN).
        var (email, deviceId, profileId) = await OnboardActiveAsync("figma1");

        // No accounts after create-profile — provisioning waits for KYC.
        var me = await Get("/api/auth/me", asUser: email, expect: HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, me.GetProperty("data").GetProperty("cif").ValueKind);
        Assert.Equal(JsonValueKind.Null, me.GetProperty("data").GetProperty("nairaAccount").ValueKind);
        Assert.Equal(0, me.GetProperty("data").GetProperty("tier").GetInt32());

        // KYC: identity + liveness come back, but no OTP — the phone is already confirmed
        // and is kept as-is.
        var initiate = await Post("/api/onboarding/initiate-kyc",
            new { deviceId, bvn = "22233344599" }, asUser: email, expect: HttpStatusCode.OK);
        var data = initiate.GetProperty("data");
        Assert.Equal(JsonValueKind.Null, data.GetProperty("otp").ValueKind);
        var sessionId = data.GetProperty("liveness").GetProperty("sessionId").GetString()!;

        // complete-kyc verifies AND provisions: cif + NGN + CAD + virtual, with the mapping.
        var complete = await Post("/api/onboarding/complete-kyc",
            new { deviceId, sessionId }, asUser: email, expect: HttpStatusCode.OK);
        var completed = complete.GetProperty("data");
        Assert.True(completed.GetProperty("bvnIsVerified").GetBoolean());
        Assert.True(completed.GetProperty("phoneNumberConfirmed").GetBoolean()); // kept from the phone leg
        Assert.Equal(1, completed.GetProperty("tier").GetInt32());
        Assert.Equal("Approved", completed.GetProperty("kycStatus").GetString());
        Assert.False(string.IsNullOrEmpty(completed.GetProperty("cif").GetString()));
        Assert.False(string.IsNullOrEmpty(completed.GetProperty("nairaAccount").GetString()));
        Assert.False(string.IsNullOrEmpty(completed.GetProperty("cadAccount").GetString()));
        var virtualAccount = completed.GetProperty("virtualAccount").GetString();
        Assert.False(string.IsNullOrEmpty(virtualAccount));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProfileSvr.Database.AppDbContext>();
        var mapping = await db.VirtualAccountMappings.SingleAsync(m => m.VirtualAccount == virtualAccount);
        Assert.Equal(profileId, mapping.ProfileId);
        Assert.Equal(completed.GetProperty("nairaAccount").GetString(), mapping.CbaAccount);
    }

    [Fact]
    public async Task Kyc_CompleteWithoutInitiate_Is422()
    {
        var (email, deviceId, _) = await OnboardAsync("kyc5");
        await Post("/api/onboarding/complete-kyc",
            new { deviceId, sessionId = "session-x" }, asUser: email,
            expect: HttpStatusCode.UnprocessableEntity);
    }

    // ---------- accounts (create-profile provisioning + login/refresh retrieval) ----------

    [Fact]
    public async Task KycFirstOrder_AccountsGeneratedAtKyc_ArePresentByCreateProfile()
    {
        var (_, _, profileId, create) = await OnboardKycActiveAsync("acct1", "22233344488");

        Assert.Equal("Approved", create.GetProperty("kycStatus").GetString());
        Assert.False(string.IsNullOrEmpty(create.GetProperty("cif").GetString()));
        Assert.False(string.IsNullOrEmpty(create.GetProperty("nairaAccount").GetString()));
        Assert.False(string.IsNullOrEmpty(create.GetProperty("cadAccount").GetString()));
        var virtualAccount = create.GetProperty("virtualAccount").GetString();
        Assert.False(string.IsNullOrEmpty(virtualAccount));
        Assert.Equal("Test Microfinance Bank", create.GetProperty("virtualAccountBank").GetString());

        // The virtual → naira mapping the credit job resolves was stored alongside.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProfileSvr.Database.AppDbContext>();
        var mapping = await db.VirtualAccountMappings.SingleAsync(m => m.VirtualAccount == virtualAccount);
        Assert.Equal(profileId, mapping.ProfileId);
        Assert.Equal(create.GetProperty("nairaAccount").GetString(), mapping.CbaAccount);
    }

    [Fact]
    public async Task Login_ReturnsAccountsAndRates_ForVerifiedProfile()
    {
        var (email, deviceId, _, _) = await OnboardKycActiveAsync("acct2", "22233344499");

        var body = await Post("/api/auth/login",
            new { username = email, password = FakeSsoClient.CorrectPassword, deviceId },
            expect: HttpStatusCode.OK);
        var data = body.GetProperty("data");

        var accounts = data.GetProperty("accounts").EnumerateArray().ToList();
        Assert.Equal(2, accounts.Count);
        Assert.Contains(accounts, a => a.GetProperty("currency").GetString() == "NGN");
        Assert.Contains(accounts, a => a.GetProperty("currency").GetString() == "CAD");
        Assert.All(accounts, a => Assert.False(string.IsNullOrEmpty(a.GetProperty("accountNumber").GetString())));

        var rates = data.GetProperty("rates").EnumerateArray().ToList();
        Assert.NotEmpty(rates);
        Assert.Contains(rates, r =>
            r.GetProperty("sourceCode").GetString() == "CAD" && r.GetProperty("targetCode").GetString() == "NGN");

        var profile = data.GetProperty("profile");
        Assert.False(string.IsNullOrEmpty(profile.GetProperty("nairaAccount").GetString()));
        Assert.False(string.IsNullOrEmpty(profile.GetProperty("cadAccount").GetString()));
        Assert.Equal("Approved", profile.GetProperty("kycStatus").GetString());
    }

    [Fact]
    public async Task Login_SelfHeals_WhenAccountProvisioningFailed()
    {
        // The banking provider is down while KYC completes and the profile is created:
        // verification and activation still succeed; the failure is ProvisioningFailed.
        factory.Accounts.FailAll = true;
        string email;
        Guid deviceId;
        try
        {
            (email, deviceId, _, var create) = await OnboardKycActiveAsync("acct3", "22233344511");
            Assert.Equal("ProvisioningFailed", create.GetProperty("kycStatus").GetString());
            Assert.Equal(JsonValueKind.Null, create.GetProperty("nairaAccount").ValueKind);
        }
        finally
        {
            factory.Accounts.FailAll = false;
        }

        // The provider is back: login retries provisioning and returns the accounts.
        var body = await Post("/api/auth/login",
            new { username = email, password = FakeSsoClient.CorrectPassword, deviceId },
            expect: HttpStatusCode.OK);
        var data = body.GetProperty("data");

        Assert.Equal(2, data.GetProperty("accounts").EnumerateArray().Count());
        var profile = data.GetProperty("profile");
        Assert.Equal("Approved", profile.GetProperty("kycStatus").GetString());
        Assert.False(string.IsNullOrEmpty(profile.GetProperty("cif").GetString()));
        Assert.False(string.IsNullOrEmpty(profile.GetProperty("nairaAccount").GetString()));
        Assert.False(string.IsNullOrEmpty(profile.GetProperty("cadAccount").GetString()));
    }

    [Fact]
    public async Task Login_ProviderDown_StillSucceeds_WithEmptyAccountsAndRates()
    {
        var (email, deviceId, _, _) = await OnboardKycActiveAsync("acct4", "22233344522");

        factory.Accounts.FailAll = true;
        try
        {
            var body = await Post("/api/auth/login",
                new { username = email, password = FakeSsoClient.CorrectPassword, deviceId },
                expect: HttpStatusCode.OK);
            var data = body.GetProperty("data");
            Assert.Equal("access-token", data.GetProperty("accessToken").GetString());
            Assert.Empty(data.GetProperty("accounts").EnumerateArray());
            Assert.Empty(data.GetProperty("rates").EnumerateArray());
        }
        finally
        {
            factory.Accounts.FailAll = false;
        }
    }

    [Fact]
    public async Task Refresh_SelfHealsAccounts_AndReturnsProfileAccountsAndRates()
    {
        // Provisioning fails at create-profile; the token refresh later self-heals it.
        factory.Accounts.FailAll = true;
        string email;
        try
        {
            (email, _, _, _) = await OnboardKycActiveAsync("acct5", "22233344533");
        }
        finally
        {
            factory.Accounts.FailAll = false;
        }

        // The SSO returns a JWT whose email claim resolves the profile.
        factory.Sso.RefreshAccessTokenOverride = UnsignedJwt(new { email });
        try
        {
            var body = await Post("/api/auth/refresh",
                new { token = "refresh-token" }, expect: HttpStatusCode.OK);
            var data = body.GetProperty("data");

            Assert.Equal("refresh-token-2", data.GetProperty("refreshToken").GetString());
            Assert.Equal(email, data.GetProperty("profile").GetProperty("emailAddress").GetString());
            Assert.Equal("Approved", data.GetProperty("profile").GetProperty("kycStatus").GetString());
            Assert.Equal(2, data.GetProperty("accounts").EnumerateArray().Count());
            Assert.NotEmpty(data.GetProperty("rates").EnumerateArray());
        }
        finally
        {
            factory.Sso.RefreshAccessTokenOverride = null;
        }
    }

    // ---------- virtual-account credit webhook + naira crediting ----------

    /// <summary>Onboards a KYC-active profile and returns its virtual + naira account numbers.</summary>
    private async Task<(string VirtualAccount, string NairaAccount)> ProvisionedAccountsAsync(string tag, string bvn)
    {
        var (_, _, _, create) = await OnboardKycActiveAsync(tag, bvn);
        return (create.GetProperty("virtualAccount").GetString()!,
                create.GetProperty("nairaAccount").GetString()!);
    }

    private async Task<int> RunCreditProcessorAsync()
    {
        using var scope = factory.Services.CreateScope();
        var processor = scope.ServiceProvider
            .GetRequiredService<ProfileSvr.Common.Accounts.IVirtualAccountCreditProcessor>();
        return await processor.ProcessPendingCreditsAsync(100);
    }

    private async Task<ProfileSvr.Domain.VirtualAccountCredit> GetCreditAsync(string reference)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProfileSvr.Database.AppDbContext>();
        return await db.VirtualAccountCredits.SingleAsync(c => c.TransactionRef == reference);
    }

    [Fact]
    public async Task CreditWebhook_StoresCredit_AndJobPostsToNairaAccount()
    {
        var (virtualAccount, nairaAccount) = await ProvisionedAccountsAsync("credit1", "22233344544");

        var reference = "VA-REF-credit1";
        var body = await Post("/webhook/virtual-account-credit", new
        {
            transactionRef = reference,
            amount = 10000m,
            account = virtualAccount,
            sourceAccount = "0123456789",
            sourceBank = "GTBank",
            senderName = "JOHN DOE",
            narration = "Transfer",
            transactionDate = DateTime.UtcNow,
            status = "SUCCESSFUL"
        }, expect: HttpStatusCode.OK);
        Assert.Equal("received", body.GetProperty("data").GetProperty("status").GetString());

        var settled = await RunCreditProcessorAsync();
        Assert.True(settled >= 1);

        // Posted to the CBA naira account with the vliquidity charges (1% + 0.5%).
        var post = factory.CbaPoster.Posts.Single(p => p.Reference == reference);
        Assert.Equal(nairaAccount, post.CbaAccount);
        Assert.Equal(10000m, post.Amount);
        Assert.Equal(100m, post.Charge);
        Assert.Equal(50m, post.ProviderCharge);
        Assert.Contains("JOHN DOE", post.Sender);

        var credit = await GetCreditAsync(reference);
        Assert.True(credit.CreditPosted);
        Assert.False(credit.PostingAbandoned);
    }

    [Fact]
    public async Task CreditWebhook_DuplicateDelivery_IsStoredOnce()
    {
        var (virtualAccount, _) = await ProvisionedAccountsAsync("credit2", "22233344555");

        var payload = new
        {
            transactionRef = "VA-REF-credit2",
            amount = 500m,
            account = virtualAccount,
            status = "SUCCESSFUL"
        };
        await Post("/webhook/virtual-account-credit", payload, expect: HttpStatusCode.OK);
        await Post("/webhook/virtual-account-credit", payload, expect: HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProfileSvr.Database.AppDbContext>();
        Assert.Equal(1, await db.VirtualAccountCredits.CountAsync(c => c.TransactionRef == "VA-REF-credit2"));
    }

    [Fact]
    public async Task CreditWebhook_InvalidPayload_IsAcknowledgedAndDropped()
    {
        var body = await Post("/webhook/virtual-account-credit",
            new { amount = 100m, status = "SUCCESSFUL" }, // no transactionRef, no account
            expect: HttpStatusCode.OK);
        Assert.Equal("ignored", body.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task CreditProcessor_UnknownVirtualAccount_RetriesThenDeadLetters()
    {
        var reference = "VA-REF-credit3";
        await Post("/webhook/virtual-account-credit", new
        {
            transactionRef = reference,
            amount = 700m,
            account = "0000000000", // no mapping exists
            status = "SUCCESSFUL"
        }, expect: HttpStatusCode.OK);

        // MaxPostAttempts defaults to 10 — the credit retries, then dead-letters.
        for (var i = 0; i < 10; i++)
            await RunCreditProcessorAsync();

        var credit = await GetCreditAsync(reference);
        Assert.False(credit.CreditPosted);
        Assert.True(credit.PostingAbandoned);
        Assert.Equal(10, credit.Attempts);
        Assert.Contains("No CBA mapping", credit.LastError);

        // A dead-lettered credit is never picked up again.
        factory.CbaPoster.Posts.Clear();
        await RunCreditProcessorAsync();
        Assert.DoesNotContain(factory.CbaPoster.Posts, p => p.Reference == reference);
    }

    [Fact]
    public async Task CreditProcessor_PermanentCbaFailure_DeadLettersImmediately()
    {
        var (virtualAccount, _) = await ProvisionedAccountsAsync("credit4", "22233344566");

        var reference = "VA-REF-credit4";
        await Post("/webhook/virtual-account-credit", new
        {
            transactionRef = reference,
            amount = 900m,
            account = virtualAccount,
            status = "SUCCESSFUL"
        }, expect: HttpStatusCode.OK);

        factory.CbaPoster.NextFailure = new HttpRequestException(
            "Bad request", null, HttpStatusCode.BadRequest);
        try
        {
            await RunCreditProcessorAsync();
        }
        finally
        {
            factory.CbaPoster.NextFailure = null;
        }

        var credit = await GetCreditAsync(reference);
        Assert.False(credit.CreditPosted);
        Assert.True(credit.PostingAbandoned); // 4xx is permanent — no retry budget spent on it
        Assert.Equal(1, credit.Attempts);
    }

    [Fact]
    public async Task CreditProcessor_IgnoresNonSuccessfulStatus()
    {
        var (virtualAccount, _) = await ProvisionedAccountsAsync("credit5", "22233344577");

        await Post("/webhook/virtual-account-credit", new
        {
            transactionRef = "VA-REF-credit5",
            amount = 300m,
            account = virtualAccount,
            status = "PENDING"
        }, expect: HttpStatusCode.OK);

        await RunCreditProcessorAsync();

        var credit = await GetCreditAsync("VA-REF-credit5");
        Assert.False(credit.CreditPosted);
        Assert.Equal(0, credit.Attempts);
    }

    [Fact]
    public async Task CreditWebhook_WithConfiguredSecret_EnforcesSignature()
    {
        const string secret = "webhook-test-secret";
        var client = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("DigitVirtual:WebhookSecret", secret)).CreateClient();

        var json = JsonSerializer.Serialize(new
        {
            transactionRef = "VA-REF-signed1",
            amount = 100m,
            account = "1234567890",
            status = "SUCCESSFUL"
        });

        // Missing/wrong signature → 401.
        using (var bad = new HttpRequestMessage(HttpMethod.Post, "/webhook/virtual-account-credit")
               { Content = new StringContent(json, Encoding.UTF8, "application/json") })
        {
            bad.Headers.Add("X-Signature", "deadbeef");
            var response = await client.SendAsync(bad);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // Correct signature (lowercase hex HMAC-SHA256 of the exact body) → accepted.
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        using (var good = new HttpRequestMessage(HttpMethod.Post, "/webhook/virtual-account-credit")
               { Content = new StringContent(json, Encoding.UTF8, "application/json") })
        {
            good.Headers.Add("X-Signature", signature);
            var response = await client.SendAsync(good);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Refresh_WithOpaqueAccessToken_ReturnsTokensWithoutProfile()
    {
        var body = await Post("/api/auth/refresh",
            new { token = "refresh-token" }, expect: HttpStatusCode.OK);
        var data = body.GetProperty("data");
        Assert.Equal("access-token-2", data.GetProperty("accessToken").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("profile").ValueKind);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("accounts").ValueKind);
    }

    /// <summary>Builds an unsigned (alg none) JWT carrying the given payload claims.</summary>
    private static string UnsignedJwt(object payload)
    {
        static string Encode(object value) =>
            Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Encode(new { alg = "none", typ = "JWT" })}.{Encode(payload)}.";
    }

    // ---------- transaction PIN ----------

    [Fact]
    public async Task Pin_SetChangeAndGuards()
    {
        var (email, deviceId, _) = await OnboardActiveAsync("pin1");

        // set
        var set = await Post("/api/profiles/set-pin",
            new { deviceId, pin = "1234" }, asUser: email, expect: HttpStatusCode.OK);
        Assert.True(set.GetProperty("data").GetProperty("hasSetTransactionPin").GetBoolean());

        // second set refused
        await Post("/api/profiles/set-pin",
            new { deviceId, pin = "9999" }, asUser: email, expect: HttpStatusCode.Conflict);

        // change with wrong current pin
        await Post("/api/profiles/pin-change",
            new { deviceId, currentPin = "0000", newPin = "5678" }, asUser: email,
            expect: HttpStatusCode.BadRequest);

        // change with correct current pin
        await Post("/api/profiles/pin-change",
            new { deviceId, currentPin = "1234", newPin = "5678" }, asUser: email,
            expect: HttpStatusCode.OK);

        // pin reset via email OTP
        var initiate = await Post("/api/profiles/initiate-pin-reset",
            new { deviceId }, asUser: email, expect: HttpStatusCode.Accepted);
        var retrievalCode = initiate.GetProperty("data").GetProperty("retrievalCode").GetString()!;
        var otp = factory.MessageCentre.LastCodeFor(email)!;
        await Post("/api/profiles/pin-reset",
            new { deviceId, retrievalCode, otp, newPin = "4321" }, asUser: email,
            expect: HttpStatusCode.OK);
    }

    [Fact]
    public async Task Pin_WrongDevice_Is403()
    {
        var (email, _, _) = await OnboardActiveAsync("pin2");
        await Post("/api/profiles/set-pin",
            new { deviceId = Guid.NewGuid(), pin = "1234" }, asUser: email,
            expect: HttpStatusCode.Forbidden);
    }

    // ---------- password flows ----------

    [Fact]
    public async Task Password_ChangeAndReset()
    {
        var (email, deviceId, _) = await OnboardActiveAsync("pw1");

        // change: wrong current → 400, correct → 200
        await Post("/api/auth/password-change",
            new { deviceId, currentPassword = "Wrong-Pass1!", newPassword = "New-Pass-2026!" },
            asUser: email, expect: HttpStatusCode.BadRequest);
        await Post("/api/auth/password-change",
            new { deviceId, currentPassword = FakeSsoClient.CorrectPassword, newPassword = "New-Pass-2026!" },
            asUser: email, expect: HttpStatusCode.OK);

        // reset via email OTP
        var initiate = await Post("/api/auth/initiate-password-reset",
            new { deviceId }, asUser: email, expect: HttpStatusCode.Accepted);
        var retrievalCode = initiate.GetProperty("data").GetProperty("retrievalCode").GetString()!;
        var otp = factory.MessageCentre.LastCodeFor(email)!;
        await Post("/api/auth/password-reset",
            new { deviceId, retrievalCode, otp, newPassword = "Reset-Pass-2026!" },
            asUser: email, expect: HttpStatusCode.OK);
    }

    // ---------- device change ----------

    [Fact]
    public async Task DeviceChange_LockedOutUser_MovesBindingWithCredentialsAndOtp()
    {
        var (email, deviceId, profileId) = await OnboardAsync("dev1");

        // The locked-out story: a brand-new, unregistered device cannot log in at all.
        // Wrong password is refused before any OTP is sent.
        await Post("/api/auth/initiate-device-change",
            new { username = email, password = "Wrong-Pass1!" },
            expect: HttpStatusCode.Unauthorized);

        // Correct credentials -> email OTP (no token, no profile id needed).
        var initiate = await Post("/api/auth/initiate-device-change",
            new { username = email, password = FakeSsoClient.CorrectPassword },
            expect: HttpStatusCode.Accepted);
        var retrievalCode = initiate.GetProperty("data").GetProperty("retrievalCode").GetString()!;
        var otp = factory.MessageCentre.LastCodeFor(email)!;

        // change-device registers the new device and moves the binding in one step.
        var change = await Post("/api/auth/change-device",
            new { username = email, retrievalCode, otp, device = NewDevice("device-dev1-new") },
            expect: HttpStatusCode.OK);
        Assert.True(change.GetProperty("data").GetProperty("deviceRecentlyLinked").GetBoolean());
        var newDevice = change.GetProperty("data").GetProperty("deviceId").GetGuid();

        // Login from the new device now succeeds; from the old one it is refused.
        await Post("/api/auth/login",
            new { username = email, password = FakeSsoClient.CorrectPassword, deviceId = newDevice },
            expect: HttpStatusCode.OK);
        await Post("/api/auth/login",
            new { username = email, password = FakeSsoClient.CorrectPassword, deviceId },
            expect: HttpStatusCode.Forbidden);

        var profile = await Get($"/api/profiles/{profileId}", expect: HttpStatusCode.OK);
        Assert.Equal(newDevice, profile.GetProperty("data").GetProperty("activeDeviceId").GetGuid());
        // The fresh binding is the transaction gate: linked-at set, recently-linked true.
        Assert.NotEqual(JsonValueKind.Null, profile.GetProperty("data").GetProperty("activeDeviceLinkedAtUtc").ValueKind);
        Assert.True(profile.GetProperty("data").GetProperty("deviceRecentlyLinked").GetBoolean());

        var devices = await Get("/api/profiles/devices", asUser: email, expect: HttpStatusCode.OK);
        var items = devices.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(2, items.Count); // old (released) + new (active)
        Assert.Single(items, i => i.GetProperty("isActive").GetBoolean());
    }

    // ---------- create-profile (final step) ----------

    [Fact]
    public async Task CreateProfile_RequiresVerifiedPhone_ThenActivates()
    {
        var (email, deviceId, _) = await OnboardAsync("create1");

        // status after verify-auth is AuthCreated
        var meBefore = await Get("/api/auth/me", asUser: email, expect: HttpStatusCode.OK);
        Assert.Equal("AuthCreated", meBefore.GetProperty("data").GetProperty("status").GetString());

        // create-profile blocked until the phone is verified
        await Post("/api/onboarding/create-profile",
            new { deviceId, firstName = "Ada", lastName = "Obi", dateOfBirth = "1990-01-01" },
            asUser: email, expect: HttpStatusCode.UnprocessableEntity);

        // verify phone, then create the profile
        var initiate = await Post("/api/onboarding/initiate-phone",
            new { phoneNumber = "+2348090000009", deviceId }, asUser: email,
            expect: HttpStatusCode.Accepted);
        var retrievalCode = initiate.GetProperty("data").GetProperty("retrievalCode").GetString()!;
        var otp = factory.MessageCentre.LastCodeFor("+2348090000009")!;
        await Post("/api/otp/verify",
            new { deviceId, retrievalCode, otp, section = "Phone" }, asUser: email,
            expect: HttpStatusCode.OK);

        var created = await Post("/api/onboarding/create-profile",
            new { deviceId, firstName = "Ada", lastName = "Obi", dateOfBirth = "1990-01-01" },
            asUser: email, expect: HttpStatusCode.OK);
        Assert.Equal("Active", created.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal("profile-active", factory.Sso.TypeClaims[email]); // SSO claim flipped

        var meAfter = await Get("/api/auth/me", asUser: email, expect: HttpStatusCode.OK);
        Assert.Equal("Active", meAfter.GetProperty("data").GetProperty("status").GetString());
    }

    // ---------- token type gating ----------

    [Fact]
    public async Task OnboardingToken_CannotReachProfileActiveEndpoints()
    {
        var (email, deviceId, _) = await OnboardAsync("gate1"); // status AuthCreated -> type onboarding

        await Post("/api/profiles/set-pin",
            new { deviceId, pin = "1234" }, asUser: email, expect: HttpStatusCode.Forbidden);
        await Post("/api/auth/password-change",
            new { deviceId, currentPassword = FakeSsoClient.CorrectPassword, newPassword = "New-Pass-2026!" },
            asUser: email, expect: HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ProfileActiveToken_CannotReachOnboardingEndpoints()
    {
        var (email, deviceId, _) = await OnboardActiveAsync("gate2"); // status Active -> type profile-active

        await Post("/api/onboarding/initiate-phone",
            new { phoneNumber = "+2348099999999", deviceId }, asUser: email,
            expect: HttpStatusCode.Forbidden);
        await Post("/api/onboarding/create-profile",
            new { deviceId, firstName = "X", lastName = "Y", dateOfBirth = "1990-01-01" },
            asUser: email, expect: HttpStatusCode.Forbidden);

        // ...but profile-active endpoints work.
        await Post("/api/profiles/set-pin",
            new { deviceId, pin = "1234" }, asUser: email, expect: HttpStatusCode.OK);
    }

    // ---------- resume (SSO account exists, no profile record) ----------

    [Fact]
    public async Task Resume_RecoversSsoOnlyAccount_AndContinuesOnboarding()
    {
        // Simulate an account created on the SSO but missing here (interrupted verify-auth).
        var email = "orphan@example.com";
        factory.Sso.ExistingEmails.Add(email);

        // Fresh onboarding is refused, pointing at the resume path.
        var conflict = await Post("/api/onboarding/initiate",
            new { emailAddress = email, device = NewDevice("device-orphan") },
            expect: HttpStatusCode.Conflict);
        Assert.Contains("resume", conflict.GetProperty("message").GetString());

        // Resume is OTP-gated: initiate sends a device-approval code to the account email.
        var initResume = await Post("/api/onboarding/initiate-resume",
            new { }, asUser: email, expect: HttpStatusCode.Accepted);
        var resumeRetrieval = initResume.GetProperty("data").GetProperty("retrievalCode").GetString()!;
        var resumeOtp = factory.MessageCentre.LastCodeFor(email)!;

        // Wrong OTP is rejected and no device/profile is created.
        var wrong = resumeOtp == "123123" ? "321321" : "123123";
        await Post("/api/onboarding/resume",
            new { retrievalCode = resumeRetrieval, otp = wrong, device = NewDevice("device-orphan2") },
            asUser: email, expect: HttpStatusCode.BadRequest);

        // Correct OTP registers the device and recreates the record in one step —
        // reclaiming the token's SourceId as the profile id.
        var originalSourceId = Guid.NewGuid();
        var resume = await PostWithSource("/api/onboarding/resume",
            new { retrievalCode = resumeRetrieval, otp = resumeOtp, device = NewDevice("device-orphan2") },
            email, originalSourceId, expect: HttpStatusCode.Created);
        Assert.True(resume.GetProperty("data").GetProperty("resumed").GetBoolean());
        Assert.Equal("AuthCreated", resume.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(originalSourceId, resume.GetProperty("data").GetProperty("profileId").GetGuid());
        var deviceId = resume.GetProperty("data").GetProperty("deviceId").GetGuid();

        // Second resume is idempotent (OTP no longer needed once the record exists).
        var again = await Post("/api/onboarding/resume",
            new { retrievalCode = resumeRetrieval, otp = resumeOtp, deviceId },
            asUser: email, expect: HttpStatusCode.OK);
        Assert.False(again.GetProperty("data").GetProperty("resumed").GetBoolean());

        // With a record in place, initiate-resume now refuses.
        await Post("/api/onboarding/initiate-resume", new { }, asUser: email,
            expect: HttpStatusCode.Conflict);

        // Onboarding now continues normally: phone leg works against the resumed record.
        var initiate = await Post("/api/onboarding/initiate-phone",
            new { phoneNumber = "+2348090000077", deviceId }, asUser: email,
            expect: HttpStatusCode.Accepted);
        var retrievalCode = initiate.GetProperty("data").GetProperty("retrievalCode").GetString()!;
        var otp = factory.MessageCentre.LastCodeFor("+2348090000077")!;
        await Post("/api/otp/verify",
            new { deviceId, retrievalCode, otp, section = "Phone" }, asUser: email,
            expect: HttpStatusCode.OK);

        var created = await Post("/api/onboarding/create-profile",
            new { deviceId, firstName = "Orphan", lastName = "Rescued", dateOfBirth = "1992-02-02" },
            asUser: email, expect: HttpStatusCode.OK);
        Assert.Equal("Active", created.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Resume_RejectsDeviceInUseByAnotherProfile()
    {
        // victim's onboarding binds their device
        var (_, victimDevice, _) = await OnboardAsync("victim1");

        // an SSO-only orphan tries to resume onto the victim's active device
        var email = "orphan2@example.com";
        factory.Sso.ExistingEmails.Add(email);

        var initResume = await Post("/api/onboarding/initiate-resume",
            new { }, asUser: email, expect: HttpStatusCode.Accepted);
        var rc = initResume.GetProperty("data").GetProperty("retrievalCode").GetString()!;
        var otp = factory.MessageCentre.LastCodeFor(email)!;

        var body = await Post("/api/onboarding/resume",
            new { retrievalCode = rc, otp, deviceId = victimDevice },
            asUser: email, expect: HttpStatusCode.Conflict);
        Assert.Contains("in use by another profile", body.GetProperty("message").GetString());

        // The victim's binding is untouched.
        var victim = await Get("/api/auth/me", asUser: "victim1@example.com", expect: HttpStatusCode.OK);
        Assert.Equal(victimDevice, victim.GetProperty("data").GetProperty("activeDeviceId").GetGuid());
    }

    // ---------- activities ----------

    [Fact]
    public async Task Activities_RecordTheFlow()
    {
        var (email, deviceId, _) = await OnboardActiveAsync("act1");
        await Post("/api/profiles/set-pin", new { deviceId, pin = "1234" }, asUser: email,
            expect: HttpStatusCode.OK);

        var feed = await Get("/api/profiles/activities", asUser: email, expect: HttpStatusCode.OK);
        var types = feed.GetProperty("data").GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("type").GetString()).ToList();

        Assert.Contains("AuthCreated", types);
        Assert.Contains("EmailVerified", types);
        Assert.Contains("PinSet", types);
        Assert.All(feed.GetProperty("data").GetProperty("items").EnumerateArray(),
            i => Assert.False(string.IsNullOrEmpty(i.GetProperty("profileId").GetString())));
    }
}
