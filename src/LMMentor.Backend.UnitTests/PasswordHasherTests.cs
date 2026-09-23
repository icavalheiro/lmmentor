using LMMentor.Backend.Data;

namespace LMMentor.Backend.UnitTests;

public class PasswordHasherTests
{
    [Fact]
    public void Verify_ReturnsTrue_ForTheOriginalPassword()
    {
        var (hash, salt) = PasswordHasher.Hash("correct horse battery staple");

        Assert.True(PasswordHasher.Verify("correct horse battery staple", hash, salt));
    }

    [Fact]
    public void Verify_ReturnsFalse_ForAWrongPassword()
    {
        var (hash, salt) = PasswordHasher.Hash("correct horse battery staple");

        Assert.False(PasswordHasher.Verify("wrong password", hash, salt));
    }

    [Fact]
    public void Verify_ReturnsFalse_ForAnotherSalt()
    {
        var (hash, _) = PasswordHasher.Hash("s3cret");
        var (_, otherSalt) = PasswordHasher.Hash("s3cret");

        Assert.False(PasswordHasher.Verify("s3cret", hash, otherSalt));
    }

    [Fact]
    public void Verify_ReturnsFalse_ForAMalformedHash()
    {
        var (_, salt) = PasswordHasher.Hash("s3cret");

        Assert.False(PasswordHasher.Verify("s3cret", "not-base64!!", salt));
    }

    [Fact]
    public void Hash_ProducesADifferentSaltPerCall()
    {
        var (firstHash, firstSalt) = PasswordHasher.Hash("s3cret");
        var (secondHash, secondSalt) = PasswordHasher.Hash("s3cret");

        Assert.NotEqual(Convert.ToBase64String(firstSalt), Convert.ToBase64String(secondSalt));
        Assert.NotEqual(firstHash, secondHash);
    }
}
