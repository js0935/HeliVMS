namespace HeliVMS.Storage.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_ProducesVerifiableNonPlaintext()
    {
        var hash = PasswordHasher.Hash("s3cr3t-P@ss");

        Assert.NotEqual("s3cr3t-P@ss", hash);
        var parts = hash.Split('$');
        Assert.Equal(4, parts.Length);
        Assert.Equal("v1", parts[0]);
        Assert.True(PasswordHasher.Verify("s3cr3t-P@ss", hash));
    }

    [Fact]
    public void Verify_WrongPassword_ReturnsFalse()
    {
        var hash = PasswordHasher.Hash("correct");

        Assert.False(PasswordHasher.Verify("wrong", hash));
    }

    [Fact]
    public void Verify_MalformedStored_ReturnsFalse()
    {
        Assert.False(PasswordHasher.Verify("x", "not-a-hash"));
        Assert.False(PasswordHasher.Verify("x", string.Empty));
        Assert.False(PasswordHasher.Verify("x", "v1$abc$!notbase64$!!"));
        Assert.False(PasswordHasher.Verify("x", "v1$0$$$$$broken"));
    }

    [Fact]
    public void Hash_ProducesRandomSaltEachTime()
    {
        var a = PasswordHasher.Hash("same");
        var b = PasswordHasher.Hash("same");

        Assert.NotEqual(a, b);
        Assert.True(PasswordHasher.Verify("same", a));
        Assert.True(PasswordHasher.Verify("same", b));
    }

    [Fact]
    public void Verify_CustomIterations_Roundtrips()
    {
        var hash = PasswordHasher.Hash("pw", iterations: 1_000);

        Assert.True(PasswordHasher.Verify("pw", hash));
        Assert.False(PasswordHasher.Verify("nope", hash));
    }
}