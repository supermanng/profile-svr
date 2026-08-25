using ProfileSvr.Common;

namespace ProfileSvr.Tests.UnitTests;

public class TransactionPinTests
{
    [Fact]
    public void HashAndVerify_RoundTrips()
    {
        var salt = TransactionPin.GenerateSalt();
        var hash = TransactionPin.Hash("1234", salt);
        Assert.True(TransactionPin.Verify("1234", salt, hash));
    }

    [Fact]
    public void Verify_RejectsWrongPin()
    {
        var salt = TransactionPin.GenerateSalt();
        var hash = TransactionPin.Hash("1234", salt);
        Assert.False(TransactionPin.Verify("4321", salt, hash));
    }

    [Fact]
    public void SamePinDifferentSalt_ProducesDifferentHashes()
    {
        var saltA = TransactionPin.GenerateSalt();
        var saltB = TransactionPin.GenerateSalt();
        Assert.NotEqual(saltA, saltB);
        Assert.NotEqual(TransactionPin.Hash("1234", saltA), TransactionPin.Hash("1234", saltB));
    }

    [Fact]
    public void Verify_FailsWithForeignSalt()
    {
        var saltA = TransactionPin.GenerateSalt();
        var saltB = TransactionPin.GenerateSalt();
        var hash = TransactionPin.Hash("1234", saltA);
        Assert.False(TransactionPin.Verify("1234", saltB, hash));
    }
}
