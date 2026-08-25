using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

    // ---------- KYC path ----------

    [Fact]
    public async Task KycFlow_LoadsProfileFromBvn_AndVerifiesToTierOne()
    {
        var (email, deviceId, profileId) = await OnboardAsync("kyc1");

        var initiate = await Post("/api/onboarding/initiate-kyc",
            new { deviceId, bvn = "22233344455" }, asUser: email,
            expect: HttpStatusCode.Accepted);
        var data = initiate.GetProperty("data");
        Assert.Equal("bvn", data.GetProperty("idType").GetString());
        Assert.EndsWith("4455", data.GetProperty("maskedPhoneNumber").GetString());
        Assert.StartsWith("*", data.GetProperty("maskedPhoneNumber").GetString());

        // Mock KYC derives the phone from the BVN; the code went to that phone.
        var kycPhone = "+234801" + "22233344455"[^7..];
        var otp = factory.MessageCentre.LastCodeFor(kycPhone)!;
        var retrievalCode = data.GetProperty("retrievalCode").GetString()!;

        var verify = await Post("/api/otp/verify",
            new { deviceId, retrievalCode, otp, section = "Kyc" }, asUser: email,
            expect: HttpStatusCode.OK);
        Assert.True(verify.GetProperty("data").GetProperty("bvnIsVerified").GetBoolean());
        Assert.Equal(1, verify.GetProperty("data").GetProperty("tier").GetInt32());

        var me = await Get("/api/auth/me", asUser: email, expect: HttpStatusCode.OK);
        Assert.Equal("Adaeze", me.GetProperty("data").GetProperty("firstName").GetString());
        Assert.True(me.GetProperty("data").GetProperty("phoneNumberConfirmed").GetBoolean());
        Assert.Equal(profileId, me.GetProperty("data").GetProperty("profileId").GetGuid());
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
