using ProfileSvr.Common;
using ProfileSvr.Domain;

namespace ProfileSvr.Tests.UnitTests;

public class OtpTests
{
    [Fact]
    public void GenerateCode_IsAlwaysSixDigits()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = Otp.GenerateCode();
            Assert.Matches(@"^\d{6}$", code);
        }
    }

    [Fact]
    public void GenerateRetrievalCode_UsesUnambiguousAlphabet()
    {
        for (var i = 0; i < 100; i++)
        {
            var rc = Otp.GenerateRetrievalCode();
            Assert.Equal(Otp.RetrievalCodeLength, rc.Length);
            Assert.Matches("^[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]+$", rc);
            Assert.DoesNotMatch("[01OI]", rc);
        }
    }

    [Fact]
    public void Hash_BindsThePurpose_SameCodeDifferentPurposeDiffers()
    {
        var email = "user@example.com";
        var forOnboarding = Otp.Hash("123456", OtpPurpose.Onboarding, email);
        var forPinReset = Otp.Hash("123456", OtpPurpose.PinReset, email);
        Assert.NotEqual(forOnboarding, forPinReset);
    }

    [Fact]
    public void Hash_BindsTheTarget_SameCodeDifferentTargetDiffers()
    {
        var a = Otp.Hash("123456", OtpPurpose.PhoneConfirmation, "+2348010000001");
        var b = Otp.Hash("123456", OtpPurpose.PhoneConfirmation, "+2348010000002");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Hash_TargetIsCaseInsensitive()
    {
        var lower = Otp.Hash("123456", OtpPurpose.EmailConfirmation, "user@example.com");
        var upper = Otp.Hash("123456", OtpPurpose.EmailConfirmation, "USER@EXAMPLE.COM");
        Assert.Equal(lower, upper);
    }

    [Fact]
    public void Verify_AcceptsMatchingCodePurposeAndTarget()
    {
        var hash = Otp.Hash("654321", OtpPurpose.DeviceChange, "user@example.com");
        Assert.True(Otp.Verify("654321", OtpPurpose.DeviceChange, "user@example.com", hash));
    }

    [Theory]
    [InlineData("654322", OtpPurpose.DeviceChange, "user@example.com")]  // wrong code
    [InlineData("654321", OtpPurpose.PasswordReset, "user@example.com")] // wrong purpose
    [InlineData("654321", OtpPurpose.DeviceChange, "other@example.com")] // wrong target
    public void Verify_RejectsAnyMismatch(string code, OtpPurpose purpose, string target)
    {
        var hash = Otp.Hash("654321", OtpPurpose.DeviceChange, "user@example.com");
        Assert.False(Otp.Verify(code, purpose, target, hash));
    }
}
