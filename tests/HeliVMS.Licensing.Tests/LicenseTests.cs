using System.Security.Cryptography;
using HeliVMS.Licensing.Crypto;

namespace HeliVMS.Licensing.Tests;

public class LicenseTests
{
    private static RSA PrivateKey => RsaPem.ParsePrivateKey(TestKeys.PrivateKeyPem);
    private static RSA PublicKey => RsaPem.ParsePublicKey(EmbeddedPublicKey.Value);

    private static string SignValidLicense(DateTime? expires = null, string? machine = null)
    {
        var payload = new LicensePayload
        {
            Machine = machine ?? MachineIdProvider.GetFingerprint(),
            IssuedUtc = DateTime.UtcNow.AddDays(-1),
            ExpiresUtc = expires ?? DateTime.UtcNow.AddDays(365),
            Cameras = 32,
            Features = ["core", "ai"],
        };
        return LicenseSerializer.Sign(payload, PrivateKey);
    }

    [Fact]
    public void RoundTrip_SignAndVerify_PayloadMatches()
    {
        var token = SignValidLicense();

        Assert.StartsWith($"{LicenseSerializer.Prefix}.", token);

        var ok = LicenseSerializer.TryVerify(token, PublicKey, out var payload, out var error);

        Assert.True(ok, error);
        Assert.NotNull(payload);
        Assert.Equal(32, payload.Cameras);
        Assert.Equal(2, payload.Ver);
    }

    [Fact]
    public void Verify_TamperedPayload_Fails()
    {
        var token = SignValidLicense();

        var parts = token.Split('.');
        var tamperedPayload = parts[1][..^1] + (parts[1][^1] == 'A' ? 'B' : 'A');
        var tampered = string.Join('.', parts[0], tamperedPayload, parts[2]);

        var ok = LicenseSerializer.TryVerify(tampered, PublicKey, out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void Verify_MalformedToken_Fails()
    {
        Assert.False(LicenseSerializer.TryVerify("NOT-A-LICENSE", PublicKey, out _, out _));
        Assert.False(LicenseSerializer.TryVerify(
            $"HELVMS-v2.?bad-signature",
            PublicKey,
            out _,
            out _));
    }

    [Fact]
    public void Validate_Expired_ReturnsExpired()
    {
        var token = SignValidLicense(expires: DateTime.UtcNow.AddDays(-1));
        var manager = new LicenseManager();

        var state = manager.Validate(token);

        Assert.Equal(LicenseStatus.Expired, state.Status);
        Assert.False(state.IsValid);
    }

    [Fact]
    public void Validate_MachineMismatch_ReturnsMismatch()
    {
        var token = SignValidLicense(machine: "DEADBEEF");
        var manager = new LicenseManager();

        var state = manager.Validate(token);

        Assert.Equal(LicenseStatus.MachineMismatch, state.Status);
        Assert.False(state.IsValid);
    }

    [Fact]
    public void Validate_ValidWithNoMachine_IsValid()
    {
        var token = SignValidLicense(machine: string.Empty);
        var manager = new LicenseManager();

        var state = manager.Validate(token);

        Assert.Equal(LicenseStatus.Valid, state.Status);
        Assert.True(state.IsValid);
        Assert.Equal(32, state.Payload!.Cameras);
    }

    [Fact]
    public void Validate_EmptyToken_NotPresent()
    {
        var manager = new LicenseManager();

        var state = manager.Validate(string.Empty);

        Assert.Equal(LicenseStatus.NotPresent, state.Status);
        Assert.False(state.IsValid);
    }

    [Fact]
    public void DefaultPath_UnderLocalAppData_NotProgramFiles()
    {
        var path = LicenseManager.DefaultPath;

        Assert.Contains("HeliVMS", path);
        Assert.DoesNotContain("Program Files", path);
    }
}